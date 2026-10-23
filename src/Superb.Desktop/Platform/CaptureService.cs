using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal enum CaptureKind { Monitor, Region }
internal sealed record CaptureTarget(CaptureKind Kind, string Label, PixelRect Bounds, string Device = "", PixelRect? MonitorBounds = null)
{
    public override string ToString() => Label;
}
internal static class CaptureService
{
    internal static List<CaptureTarget> Monitors()
    {
        var targets = new List<CaptureTarget>();
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Native.Rect r, nint data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            if (Native.GetMonitorInfo(monitor, ref info))
                targets.Add(new(CaptureKind.Monitor, $"显示器 {info.Device} · {r.Width} × {r.Height}", ToRect(r), info.Device));
            return true;
        }, 0);
        return targets;
    }
    internal static CaptureTarget MonitorAt(Native.Point point, IEnumerable<CaptureTarget> monitors) => monitors.FirstOrDefault(m =>
        point.X >= m.Bounds.X && point.Y >= m.Bounds.Y && point.X < (long)m.Bounds.X + m.Bounds.Width && point.Y < (long)m.Bounds.Y + m.Bounds.Height)
        ?? throw new SafeFailure("capture_monitor_lost");
    internal static CaptureTarget FromCorners(Native.Point first, Native.Point second, IEnumerable<CaptureTarget> monitors)
    {
        var display = MonitorAt(first, monitors);
        if (MonitorAt(second, monitors).Device != display.Device) throw new SafeFailure("capture_cross_monitor");
        var rect = new PixelRect(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));
        if (rect.Width < 5 || rect.Height < 5) throw new SafeFailure("capture_region_too_small");
        ImageProcessor.ValidateSize(rect.Width, rect.Height);
        return display with { Kind = CaptureKind.Region, Bounds = rect, MonitorBounds = display.Bounds, Label = $"两点截图 · {rect.Width} × {rect.Height}" };
    }
    public static Payload Capture(CaptureTarget target)
    {
        var bounds = Validate(target);
        ImageProcessor.ValidateSize(bounds.Width, bounds.Height);
        var screen = Native.GetDC(0);
        var memory = Native.CreateCompatibleDC(screen);
        if (screen == 0 || memory == 0)
        {
            if (memory != 0) Native.DeleteDC(memory);
            if (screen != 0) Native.ReleaseDC(0, screen);
            throw new SafeFailure("capture_unavailable");
        }
        nint bitmap = 0, previous = 0, bits = 0;
        try
        {
            var info = new Native.BitmapInfo { Size = (uint)Marshal.SizeOf<Native.BitmapInfo>(), Width = bounds.Width,
                Height = -bounds.Height, Planes = 1, BitCount = 32 };
            bitmap = Native.CreateDIBSection(screen, ref info, 0, out bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new SafeFailure("capture_allocate_failed");
            previous = Native.SelectObject(memory, bitmap);
            var success = Native.BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020);
            if (!success) throw new SafeFailure("capture_failed");
            if (Validate(target) != bounds) throw new SafeFailure("capture_target_changed");
            var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            try
            {
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                var visible = false;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > 3 || pixels[i + 1] > 3 || pixels[i + 2] > 3) visible = true;
                    pixels[i + 3] = 255;
                }
                if (!visible) throw new SafeFailure("capture_black_frame");
                var source = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgra32, null, pixels, bounds.Width * 4);
                return ImageProcessor.Encode(source);
            }
            finally { Array.Clear(pixels); }
        }
        finally
        {
            // Best effort wipe of our GDI backing buffer before releasing it.
            if (bits != 0)
            {
                var zero = new byte[Math.Min(65536, checked(bounds.Width * bounds.Height * 4))];
                var size = checked(bounds.Width * bounds.Height * 4);
                for (var offset = 0; offset < size; offset += zero.Length)
                    Marshal.Copy(zero, 0, bits + offset, Math.Min(zero.Length, size - offset));
            }
            if (previous != 0) Native.SelectObject(memory, previous);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(0, screen);
        }
    }
    private static PixelRect Validate(CaptureTarget target)
    {
        var monitor = Monitors().FirstOrDefault(x => x.Device == target.Device)
            ?? throw new SafeFailure("capture_monitor_lost");
        var b = target.Bounds;
        var m = monitor.Bounds;
        if (target.Kind == CaptureKind.Monitor && b != m) throw new SafeFailure("capture_geometry_changed");
        if (target.Kind == CaptureKind.Region && target.MonitorBounds != m) throw new SafeFailure("capture_geometry_changed");
        if (b.Width <= 0 || b.Height <= 0 || b.X < m.X || b.Y < m.Y ||
            (long)b.X + b.Width > (long)m.X + m.Width || (long)b.Y + b.Height > (long)m.Y + m.Height)
            throw new SafeFailure("capture_region_out_of_bounds");
        return b;
    }
    private static PixelRect ToRect(Native.Rect r) => new(r.Left, r.Top, r.Width, r.Height);
}
