using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Runtime.CompilerServices;

namespace Superb.Desktop.Platform;

// WPF popups have their own HWND; the owner's capture exclusion does not cover them.
// Protect only this application's WPF presentation surfaces. This does not hide windows
// from enumeration, and does not establish compatibility with a particular capture chain.
public static class TransientSurfaceProtection
{
    private static bool initialized, failurePending;
    private static readonly Dictionary<HwndSource, UIElement> surfaces = [];
    private static readonly ConditionalWeakTable<UIElement, object> suppressed = new();
    private static readonly ConditionalWeakTable<Popup, object> attached = new();
    public static readonly DependencyProperty IsProtectedProperty = DependencyProperty.RegisterAttached(
        "IsProtected", typeof(bool), typeof(TransientSurfaceProtection), new PropertyMetadata(false, ProtectionChanged));
    public static void SetIsProtected(DependencyObject target, bool value) => target.SetValue(IsProtectedProperty, value);
    public static bool GetIsProtected(DependencyObject target) => (bool)target.GetValue(IsProtectedProperty);

    private static void ProtectionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ComboBox or MenuItem)
        {
            var control = (Control)target;
            control.Loaded -= AttachPopup;
            if (args.NewValue is true) { control.Loaded += AttachPopup; if (control.IsLoaded) AttachPopup(control, new RoutedEventArgs()); }
            if (control is ComboBox combo)
            {
                combo.DropDownOpened -= ComboOpened;
                if (args.NewValue is true) combo.DropDownOpened += ComboOpened;
            }
        }
        else if (target is ToolTip or ContextMenu)
        {
            var element = (UIElement)target;
            PresentationSource.RemoveSourceChangedHandler(element, SurfaceSourceChanged);
            if (args.NewValue is true) PresentationSource.AddSourceChangedHandler(element, SurfaceSourceChanged);
        }
    }

    internal static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(SurfaceOpened), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(SurfaceOpened), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(SubmenuOpened), true);
    }

    private static void AttachPopup(object sender, RoutedEventArgs args)
    {
        if (sender is not Control control || control.Template?.FindName("PART_Popup", control) is not Popup popup || attached.TryGetValue(popup, out _)) return;
        attached.Add(popup, new object());
        if (popup.Child is { } child)
            PresentationSource.AddSourceChangedHandler(child, (_, _) => Protect(child));
        // The HWND may be recreated on every open; never cache protection by control alone.
        popup.Opened += (_, _) => { if (popup.Child is { } content) Protect(content, ownedOpen: true); };
    }

    private static void ComboOpened(object? sender, EventArgs args)
    {
        if (sender is not ComboBox combo) return;
        AttachPopup(combo, new RoutedEventArgs());
        if (combo.Template?.FindName("PART_Popup", combo) is Popup { Child: { } child }) Protect(child, ownedOpen: true);
    }

    private static void SurfaceOpened(object sender, RoutedEventArgs args)
    {
        if (sender is UIElement element) Protect(element, ownedOpen: true);
    }

    private static void SurfaceSourceChanged(object sender, SourceChangedEventArgs args)
    {
        if (sender is UIElement element) Protect(element);
    }

    private static void SubmenuOpened(object sender, RoutedEventArgs args)
    {
        AttachPopup(sender, args);
        if (sender is MenuItem item && item.Template?.FindName("PART_Popup", item) is Popup { Child: { } child }) Protect(child, ownedOpen: true);
    }

    private static void Protect(UIElement element, bool ownedOpen = false)
    {
        if (PresentationSource.FromVisual(element) is not HwndSource source || source.IsDisposed || source.Handle == 0 ||
            source.RootVisual is not UIElement root || root is Window) return;
        if (surfaces.ContainsKey(source) && !ownedOpen) return;
        if (!surfaces.ContainsKey(source))
        {
            surfaces.Add(source, root);
            source.Disposed += SurfaceDisposed;
        }
        var enabled = 1;
        Native.DwmSetWindowAttribute(source.Handle, 11, ref enabled, sizeof(int));
        Native.DwmSetWindowAttribute(source.Handle, 12, ref enabled, sizeof(int));
        if (!Native.SetWindowDisplayAffinity(source.Handle, 0x11) || !HasAffinity(source.Handle))
        {
            // Suppress content synchronously; the owner's next guard pauses the session.
            root.Visibility = Visibility.Collapsed;
            suppressed.GetValue(root, _ => new object());
            failurePending = true;
        }
        else if (ownedOpen && suppressed.Remove(root)) root.Visibility = Visibility.Visible;
    }

    private static bool HasAffinity(nint handle) => Native.IsWindow(handle) &&
        Native.DwmIsCompositionEnabled(out var enabled) == 0 && enabled &&
        Native.GetWindowDisplayAffinity(handle, out var affinity) && affinity == 0x11;

    private static void SurfaceDisposed(object? sender, EventArgs args)
    {
        if (sender is HwndSource source) { source.Disposed -= SurfaceDisposed; surfaces.Remove(source); }
    }

    internal static bool Check()
    {
        var valid = !failurePending;
        failurePending = false;
        foreach (var (source, root) in surfaces.ToArray())
        {
            if (source.IsDisposed) { surfaces.Remove(source); continue; }
            if (!Native.IsWindowVisible(source.Handle)) continue;
            if (HasAffinity(source.Handle)) continue;
            root.Visibility = Visibility.Collapsed;
            suppressed.GetValue(root, _ => new object());
            valid = false;
        }
        return valid;
    }
}
