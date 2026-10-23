using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class PassThroughTests
{
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Settle(ReaderWindow reader)
    {
        reader.UpdateLayout(); Pump(); reader.RefreshInputRegions(); Pump(); Native.DwmFlush();
    }
    private static Point Center(FrameworkElement element, Window relativeTo) =>
        element.TransformToAncestor(relativeTo).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
    private static nint Hit(Window window, Point point)
    {
        var screen = window.PointToScreen(point);
        return Native.GetAncestor(WindowFromPoint(new() { X = (int)screen.X, Y = (int)screen.Y }), 2);
    }
    private static bool Contains(Window window, nint handle, Point point)
    {
        var region = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            Check(GetWindowRgn(handle, region) != 0, "Missing native window region");
            var dpi = VisualTreeHelper.GetDpi(window);
            return PtInRegion(region, (int)(point.X * dpi.DpiScaleX), (int)(point.Y * dpi.DpiScaleY));
        }
        finally { Native.DeleteObject(region); }
    }
    private static byte[] Render(Visual visual, int width, int height, string? path = null)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path); encoder.Save(file);
        }
        var bytes = new byte[width * height * 4]; bitmap.CopyPixels(bytes, width * 4, 0); return bytes;
    }
    internal static int RunTarget()
    {
        // A separate process is necessary: HTTRANSPARENT can appear to work inside one thread.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var area = SystemParameters.WorkArea;
        var window = new Window { Title = "Superb synthetic pass-through target", ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Topmost = true,
            Left = area.Left, Top = area.Top, Width = area.Width, Height = area.Height,
            Background = Brushes.DarkSlateBlue };
        window.Show(); Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64());
        _ = Task.Run(() => { Console.ReadLine(); app.Dispatcher.BeginInvoke(() => app.Shutdown()); });
        app.Run(); return 0;
    }

    internal static void Add(List<(string, Action)> tests, string[] args)
    {
        var renderIndex = Array.IndexOf(args, "--render-dir");
        var renderDirectory = renderIndex >= 0 && renderIndex + 1 < args.Length ? args[renderIndex + 1] : null;
        tests.Add(("Partial pass-through reaches a different process while controls and edit selection retain native input regions", () =>
        {
            using var target = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { Assembly.GetExecutingAssembly().Location, "--pass-through-target" },
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, CreateNoWindow = true
            })!;
            var reader = new ReaderWindow { Left = SystemParameters.WorkArea.Left + 30, Top = SystemParameters.WorkArea.Top + 30,
                Width = 420, Height = 540 };
            try
            {
                var line = target.StandardOutput.ReadLineAsync();
                Check(line.Wait(10000), "Receiver startup timed out");
                var targetHandle = (nint)long.Parse(line.Result!);
                var before = Native.GetForegroundWindow();
                reader.Show(); reader.SetActions(true, false, true, true, false, true);
                reader.Render(string.Join("\n\n", Enumerable.Range(1, 80).Select(i => $"Synthetic answer paragraph {i}.")), "synthetic");
                Settle(reader);
                var inputHandle = new WindowInteropHelper(reader).Handle;
                var answer = (AnswerView)reader.FindName("Answer");
                var point = Center(answer, reader);
                Check(Hit(reader, point) == targetHandle, "Read-only answer did not pass through to the separate process");
                Check(Hit(reader, new Point(3, 3)) == targetHandle, "Window border did not pass through");
                Check(Hit(reader, Center((Button)reader.FindName("ScreenSendButton"), reader)) == inputHandle, "Screen send button lost native input");
                var scroll = Find<ScrollBar>(answer, b => b.IsVisible && b.Orientation == Orientation.Vertical);
                Check(Hit(reader, Center(scroll, reader)) == inputHandle, "Answer scrollbar lost native input");
                Check(!Contains(reader, inputHandle, point) && Contains(reader, reader.DisplayLayerHandle, point), "Answer regions overlap or leave a gap");
                Check(Native.GetForegroundWindow() == before, "Creating the display companion stole foreground");

                ((Expander)reader.FindName("ComposerExpander")).IsExpanded = true; Settle(reader);
                Check(Hit(reader, Center((TextBox)reader.FindName("DraftText"), reader)) == inputHandle, "Draft input lost its native target");
                ((Expander)reader.FindName("ComposerExpander")).IsExpanded = false; Settle(reader);
                ((Button)reader.FindName("AppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(reader);
                var slider = (Slider)reader.FindName("PanelWidth"); slider.BringIntoView(); Settle(reader);
                Check(Hit(reader, Center(slider, reader)) == inputHandle, "Appearance slider is not interactive");
                ((Button)reader.FindName("AppearanceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(reader);
                point = Center(answer, reader);

                Check(reader.SetMode(true), "Cannot enter editing"); Settle(reader);
                Check(Hit(reader, point) == inputHandle, "Editing did not restore answer selection hit target");
                Check(reader.SetMode(false, true), "Cannot enter full pass-through"); Settle(reader);
                Check(Hit(reader, Center((Button)reader.FindName("ScreenSendButton"), reader)) == targetHandle, "Full pass-through still intercepts buttons");
                Check(reader.SetMode(false), "Cannot restore reading"); Settle(reader);
                reader.Left += 40; reader.Top += 20; reader.Width = 500; Settle(reader);
                Check(Hit(reader, Center(answer, reader)) == targetHandle, "Moved answer no longer passes through");
                Check(Hit(reader, Center((Button)reader.FindName("ScreenSendButton"), reader)) == inputHandle, "Moved button region stale");
                Check(Native.GetWindowRect(inputHandle, out var inputRect) && Native.GetWindowRect(reader.DisplayLayerHandle, out var displayRect) && inputRect.Equals(displayRect), "Display and input HWNDs became misaligned");
                reader.Hide(); Pump();
                Check(!Native.IsWindowVisible(reader.DisplayLayerHandle), "Display layer survived hide");
                reader.Show(); Settle(reader);
                Check(reader.Check() && Native.IsWindowVisible(reader.DisplayLayerHandle), "Display protection or visibility lost after reshow");
            }
            finally
            {
                reader.Finish();
                if (!target.HasExited) { target.StandardInput.WriteLine("quit"); if (!target.WaitForExit(5000)) target.Kill(); }
            }
        }));
        tests.Add(("Display companion renders live content with complementary regions and fails closed on protection loss", () =>
        {
            var reader = new ReaderWindow { Width = 420, Height = 540 };
            try
            {
                reader.Show(); reader.Render("# Synthetic content\n\nVisible passive text", "test"); Settle(reader);
                var display = (Window)HwndSource.FromHwnd(reader.DisplayLayerHandle)!.RootVisual;
                var surface = (FrameworkElement)reader.FindName("WindowSurface");
                var first = Render(display, 420, 540, renderDirectory is null ? null : Path.Combine(renderDirectory, "passive-display.png"));
                Check(first.Any(b => b != 0), "Display companion is blank");
                var original = Render(surface, 420, 540);
                Check(first.SequenceEqual(original), "Live display differs from source visual");
                reader.Render("# Changed content\n\nStreaming answer update", "updated"); Settle(reader);
                var second = Render(display, 420, 540);
                Check(!first.SequenceEqual(second), "Display companion did not update with source");
                var hwnd = new WindowInteropHelper(reader).Handle;
                for (var y = 2; y < 540; y += 13)
                    for (var x = 2; x < 420; x += 13)
                        Check(Contains(reader, hwnd, new(x, y)) != Contains(reader, reader.DisplayLayerHandle, new(x, y)), "Display and input regions are not complementary");
                var lost = false; reader.ProtectionLost += () => lost = true;
                Check(Native.SetWindowDisplayAffinity(reader.DisplayLayerHandle, 0), "Could not simulate companion protection loss");
                Check(!reader.Check() && lost && !Native.IsWindowVisible(reader.DisplayLayerHandle), "Companion protection loss did not close sensitive display");
                Check(((AnswerView)reader.FindName("Answer")).Text == "", "Answer not cleared after protection loss");
            }
            finally { reader.Finish(); }
        }));
    }

    private static T Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        T? Search(DependencyObject node)
        {
            if (node is T match && predicate(match)) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (Search(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
            return null;
        }
        return Search(root) ?? throw new Exception("Missing " + typeof(T).Name);
    }
}
