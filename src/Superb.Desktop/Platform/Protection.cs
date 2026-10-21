using System.Windows;
using System.Windows.Interop;

namespace Superb.Desktop.Platform;

internal sealed class Protection(Window window, bool noActivate)
{
    private bool blocksActivation = noActivate;
    private readonly bool floating = noActivate;
    private bool through;
    public nint Handle { get; private set; }
    public bool Install()
    {
        Handle = new WindowInteropHelper(window).Handle;
        if (floating)
        {
            var style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
            Native.SetWindowLongPtr(Handle, -20, (nint)((style | 0x08000080L) & ~0x00040000L));
            HwndSource.FromHwnd(Handle)?.AddHook(NoActivateHook);
        }
        var enabled = 1;
        Native.DwmSetWindowAttribute(Handle, 11, ref enabled, sizeof(int)); // DISALLOW_PEEK
        Native.DwmSetWindowAttribute(Handle, 12, ref enabled, sizeof(int)); // EXCLUDED_FROM_PEEK
        return RefreshDisplayAffinity();
    }
    // Explicit window visibility transitions can reset display affinity on Windows.
    // Only restore it at those owned transitions; ordinary Check remains fail-closed.
    public bool RefreshDisplayAffinity() => Handle != 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        Native.SetWindowDisplayAffinity(Handle, 0x11) && Check();
    public bool Check() => Handle != 0 && Native.IsWindow(Handle) &&
        (!floating || ((Native.GetWindowLongPtr(Handle, -20).ToInt64() & 0x08000080L) == (blocksActivation ? 0x08000080L : 0x80L) &&
            (!through || (Native.GetWindowLongPtr(Handle, -20).ToInt64() & 0x80020L) == 0x80020L))) &&
        Native.DwmIsCompositionEnabled(out var enabled) == 0 && enabled &&
        Native.GetWindowDisplayAffinity(Handle, out var affinity) && affinity == 0x11;
    public bool SetInteraction(bool interactive, bool clickThrough = false)
    {
        if (!floating || Handle == 0 || !Check() || (interactive && clickThrough)) return false;
        var style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
        // WPF owns WS_EX_LAYERED through AllowsTransparency. Preserve it instead of toggling it
        // behind the compositor's back (WPF restores it during a frame/style update).
        if (clickThrough && (style & 0x80000L) == 0) return false;
        style = (style | 0x80L) & ~0x08000020L;
        if (!interactive) style |= 0x08000000L;
        if (clickThrough) style |= 0x20L;
        Native.SetWindowLongPtr(Handle, -20, (nint)style);
        blocksActivation = !interactive; through = clickThrough;
        return Native.SetWindowPos(Handle, (nint)(-1), 0, 0, 0, 0, 0x0033) && RefreshDisplayAffinity();
    }
    private nint NoActivateHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (blocksActivation && msg == 0x21) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE; includes hover activation.
        return 0;
    }
}
