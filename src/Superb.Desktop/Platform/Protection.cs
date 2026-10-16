using System.Windows;
using System.Windows.Interop;

namespace Superb.Desktop.Platform;

internal sealed class Protection(Window window, bool noActivate)
{
    public nint Handle { get; private set; }
    public bool Install()
    {
        Handle = new WindowInteropHelper(window).Handle;
        if (noActivate)
        {
            var style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
            Native.SetWindowLongPtr(Handle, -20, (nint)((style | 0x08000080L) & ~0x00040000L));
            HwndSource.FromHwnd(Handle)?.AddHook(NoActivateHook);
        }
        var enabled = 1;
        Native.DwmSetWindowAttribute(Handle, 11, ref enabled, sizeof(int)); // DISALLOW_PEEK
        Native.DwmSetWindowAttribute(Handle, 12, ref enabled, sizeof(int)); // EXCLUDED_FROM_PEEK
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
               Native.SetWindowDisplayAffinity(Handle, 0x11) && Check();
    }
    public bool Check() => Handle != 0 && Native.IsWindow(Handle) &&
        (!noActivate || (Native.GetWindowLongPtr(Handle, -20).ToInt64() & 0x08000080L) == 0x08000080L) &&
        Native.DwmIsCompositionEnabled(out var enabled) == 0 && enabled &&
        Native.GetWindowDisplayAffinity(Handle, out var affinity) && affinity == 0x11;
    private static nint NoActivateHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE; includes hover activation.
        return 0;
    }
}
