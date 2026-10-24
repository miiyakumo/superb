using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class ReaderWindow
{
    internal event Action? LayoutChanged;
    private bool applyingAppearance, appearanceReady, answerMode, composerLayoutPending;
    private double backgroundOpacity = 1;
    private string themeId = Theme.InkId;
    private string accentHex = "";
    private string customBase = "";
    private string customInk = "";
    private void InitializeAppearance()
    {
        appearanceReady = true;
        SizeChanged += (_, _) => NotifyLayoutChanged();
        LocationChanged += (_, _) => NotifyLayoutChanged();
        SetBackgroundOpacity(1);
    }
    internal void BeginAnswer()
    {
        if (answerMode) return;
        answerMode = true; ComposerExpander.IsExpanded = false;
        UpdateControls();
    }
    private Rect CurrentWorkArea()
    {
        if (protection.Handle != 0)
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            if (Native.GetMonitorInfo(Native.MonitorFromWindow(protection.Handle, 2), ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return new(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY, info.Work.Width / dpi.DpiScaleX, info.Work.Height / dpi.DpiScaleY);
            }
        }
        return SystemParameters.WorkArea;
    }
    private void ResizeTo(double width, double height)
    {
        var area = CurrentWorkArea();
        Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, Math.Min(MaxWidth, area.Width)));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, Math.Min(MaxHeight, area.Height)));
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
    private void SetBackgroundOpacity(double value)
    {
        backgroundOpacity = Math.Clamp(value, 0.25, 1);
        // Keep text fully opaque; never change native layered styles or the Window's opacity.
        var alpha = (byte)Math.Round(backgroundOpacity * 255);
        var resources = Application.Current.Resources;
        var baseColor = ((SolidColorBrush)resources["Base"]).Color;
        var surface = ((SolidColorBrush)resources["Surface"]).Color;
        var accent = ((SolidColorBrush)resources["Accent"]).Color;
        // Scope translucent backgrounds to this reader's attachments, code blocks
        // and normal button hover states.
        // Keep Accent/Ink/OnAccent opaque because they are also used for foregrounds.
        Resources["Surface"] = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
        Resources["WorkspaceAccentBackground"] = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
        WindowSurface.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        AnswerSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Surface");
    }
    private void ApplyPalette()
    {
        if (!Theme.TryResolve(themeId, accentHex, customBase, customInk, out var palette, out _))
        {
            themeId = Theme.InkId; accentHex = ""; customBase = ""; customInk = "";
            Theme.TryResolve(themeId, accentHex, customBase, customInk, out palette, out _);
        }
        Theme.Apply(palette);
        SetBackgroundOpacity(backgroundOpacity);
        if (Answer.Text.Length > 0) Answer.Restyle();
    }
    private void NotifyLayoutChanged() { if (appearanceReady && !applyingAppearance && IsLoaded) LayoutChanged?.Invoke(); }
    internal void CollapseInput() => ComposerExpander.IsExpanded = false;
    internal Rect AppearanceWorkArea => CurrentWorkArea();
    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeTo(Width + e.HorizontalChange, Height + e.VerticalChange);
        NotifyLayoutChanged(); e.Handled = true;
    }
    private void WorkArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueComposerLayout();
    }
    private void QueueComposerLayout()
    {
        if (!appearanceReady || composerLayoutPending) return;
        composerLayoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            composerLayoutPending = false;
            ComposerScroll.MaxHeight = Math.Clamp(WorkArea.ActualHeight - 220 - WorkArea.RowDefinitions[2].ActualHeight, 0, 220);
        }));
    }
}
