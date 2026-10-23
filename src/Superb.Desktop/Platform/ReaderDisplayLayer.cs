using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Superb.Desktop.Platform;

// The source HWND is restricted to actual input regions. A permanently mouse-transparent
// companion paints the complementary area using the same live visual (no bitmap polling).
internal sealed class ReaderDisplayLayer
{
    private readonly Window source;
    private readonly Window display;
    private readonly VisualBrush brush;
    private readonly Protection protection;
    private Native.Rect[]? previous;
    private int previousWidth, previousHeight;
    private bool updating, failed, closed;
    internal nint Handle => protection.Handle;

    internal ReaderDisplayLayer(Window source, Visual visual)
    {
        this.source = source;
        brush = new VisualBrush(visual) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
        display = new Window
        {
            Title = "Superb · 显示层", Owner = source, ShowActivated = false, ShowInTaskbar = false,
            AllowsTransparency = true, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent, Topmost = true, Focusable = false,
            Content = new Rectangle { Fill = brush, IsHitTestVisible = false },
            Left = source.Left, Top = source.Top, Width = source.Width, Height = source.Height
        };
        protection = new(display, true);
        new WindowInteropHelper(display).EnsureHandle();
        failed = !protection.Install() || !protection.SetInteraction(false, true);
        display.Closed += (_, _) => { closed = true; brush.Visual = null; };
        // Never expose the source as an unshaped, full-window mouse target during first show.
        var empty = Native.CreateRectRgn(0, 0, 0, 0);
        if (empty == 0 || Native.SetWindowRgn(new WindowInteropHelper(source).Handle, empty, true) == 0)
        {
            if (empty != 0) Native.DeleteObject(empty);
            failed = true;
        }
    }

    internal bool Check() => !failed && !closed && protection.Check();
    internal void Invalidate() => Fail();
    internal void Hide()
    {
        if (closed) return;
        display.Hide();
        if (!protection.RefreshDisplayAffinity()) failed = true;
    }

    internal bool Update(IReadOnlyList<Rect> regions)
    {
        if (updating || !source.IsVisible) return !failed;
        if (!Check()) return Fail();
        updating = true;
        try
        {
            var sourceHandle = new WindowInteropHelper(source).Handle;
            if (!Native.GetWindowRect(sourceHandle, out var bounds)) return Fail();
            var dpi = VisualTreeHelper.GetDpi(source);
            var pixels = regions.Select(r => new Native.Rect
            {
                Left = Math.Max(0, (int)Math.Floor(r.Left * dpi.DpiScaleX)),
                Top = Math.Max(0, (int)Math.Floor(r.Top * dpi.DpiScaleY)),
                Right = Math.Min(bounds.Width, (int)Math.Ceiling(r.Right * dpi.DpiScaleX)),
                Bottom = Math.Min(bounds.Height, (int)Math.Ceiling(r.Bottom * dpi.DpiScaleY))
            }).Where(r => r.Width > 0 && r.Height > 0).ToArray();
            brush.Viewbox = new Rect(source.RenderSize);
            // Physical coordinates keep the two HWNDs aligned across monitor DPI changes.
            if (!Native.GetWindowRect(Handle, out var old) || !old.Equals(bounds))
                if (!Native.SetWindowPos(Handle, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014)) return Fail();
            if (previous is null || previousWidth != bounds.Width || previousHeight != bounds.Height || !previous.SequenceEqual(pixels))
            {
                if (!ApplyRegions(sourceHandle, bounds.Width, bounds.Height, pixels)) return Fail();
                previous = pixels; previousWidth = bounds.Width; previousHeight = bounds.Height;
            }
            if (!display.IsVisible)
            {
                display.Show();
                if (!protection.RefreshDisplayAffinity()) return Fail();
            }
            return Check();
        }
        finally { updating = false; }
    }

    private bool ApplyRegions(nint sourceHandle, int width, int height, Native.Rect[] rectangles)
    {
        var input = Native.CreateRectRgn(0, 0, 0, 0);
        var passive = Native.CreateRectRgn(0, 0, width, height);
        try
        {
            if (input == 0 || passive == 0) return false;
            foreach (var r in rectangles)
            {
                var part = Native.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
                if (part == 0) return false;
                try { if (Native.CombineRgn(input, input, part, 2) == 0) return false; } // RGN_OR
                finally { Native.DeleteObject(part); }
            }
            // Complementary regions prevent double opacity at controls and seams.
            if (Native.CombineRgn(passive, passive, input, 4) == 0) return false; // RGN_DIFF
            if (Native.SetWindowRgn(Handle, passive, true) == 0) return false;
            passive = 0; // successful SetWindowRgn transfers ownership to Windows
            if (Native.SetWindowRgn(sourceHandle, input, true) == 0) return false;
            input = 0;
            return true;
        }
        finally
        {
            if (input != 0) Native.DeleteObject(input);
            if (passive != 0) Native.DeleteObject(passive);
        }
    }

    private bool Fail()
    {
        failed = true;
        if (!closed) display.Hide();
        return false;
    }
}
