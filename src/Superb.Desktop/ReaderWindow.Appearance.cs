using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
        SizeChanged += (_, _) => { SyncAppearanceControls(); NotifyLayoutChanged(); };
        LocationChanged += (_, _) => NotifyLayoutChanged();
        DraftText.GotKeyboardFocus += (_, _) => QueueComposerLayout();
        SyncAppearanceControls(); SetBackgroundOpacity(1);
    }
    internal void BeginAnswer()
    {
        if (answerMode) return;
        answerMode = true; ComposerExpander.IsExpanded = false; AppearancePanel.Visibility = Visibility.Collapsed;
        SetEditingControls();
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
        WindowSurface.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        AnswerSurface.Background = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
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
    private void CommitTheme()
    {
        applyingAppearance = true;
        try { ApplyPalette(); }
        finally { applyingAppearance = false; }
        SyncAppearanceControls(); NotifyLayoutChanged();
    }
    private void SyncAppearanceControls()
    {
        if (!appearanceReady || applyingAppearance) return;
        applyingAppearance = true;
        try
        {
            var area = CurrentWorkArea();
            PanelWidth.Maximum = Math.Max(MinWidth, Math.Min(MaxWidth, area.Width));
            PanelHeight.Maximum = Math.Max(MinHeight, Math.Min(MaxHeight, area.Height));
            PanelWidth.Value = Width; PanelHeight.Value = Height;
            PanelOpacity.Value = backgroundOpacity; PanelTextSize.Value = Answer.FontSize;
            MarkTheme(ThemeInk, Theme.InkId); MarkTheme(ThemeMist, Theme.MistId);
            MarkTheme(ThemePaper, Theme.PaperId); MarkTheme(ThemePine, Theme.PineId);
            var effective = Theme.EffectiveAccent(themeId, accentHex);
            foreach (var button in AccentSwatches.Children.OfType<Button>())
            {
                var selected = string.Equals(button.Tag as string, effective, StringComparison.OrdinalIgnoreCase);
                button.BorderBrush = (Brush)FindResource(selected ? "Ink" : "Line");
                button.BorderThickness = new Thickness(selected ? 2 : 1);
            }
            if (!CustomBaseBox.IsKeyboardFocused && !CustomInkBox.IsKeyboardFocused && !CustomAccentBox.IsKeyboardFocused)
            {
                CustomBaseBox.Text = customBase; CustomInkBox.Text = customInk; CustomAccentBox.Text = accentHex;
                ThemeError.Text = "";
            }
        }
        finally { applyingAppearance = false; }
    }
    private void MarkTheme(Button button, string id)
    {
        var selected = customBase.Length == 0 && customInk.Length == 0 && themeId == id;
        if (selected) button.Style = (Style)FindResource("WorkspacePrimary");
        else button.ClearValue(FrameworkElement.StyleProperty);
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }
    private void NotifyLayoutChanged() { if (appearanceReady && !applyingAppearance && IsLoaded) LayoutChanged?.Invoke(); }
    private void Appearance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!appearanceReady || applyingAppearance) return;
        applyingAppearance = true;
        try
        {
            if (ReferenceEquals(sender, PanelWidth)) ResizeTo(e.NewValue, Height);
            else if (ReferenceEquals(sender, PanelHeight)) ResizeTo(Width, e.NewValue);
            else if (ReferenceEquals(sender, PanelOpacity)) SetBackgroundOpacity(e.NewValue);
            else if (ReferenceEquals(sender, PanelTextSize)) Answer.FontSize = Math.Round(e.NewValue);
        }
        finally { applyingAppearance = false; }
        SyncAppearanceControls(); NotifyLayoutChanged();
    }
    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        var editingDraft = DraftText.IsKeyboardFocusWithin;
        AppearancePanel.Visibility = AppearancePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (AppearancePanel.Visibility == Visibility.Visible)
        {
            // Collapsing the focused editor hides the draft and makes WPF move focus.
            if (!editingDraft) ComposerExpander.IsExpanded = false;
            SyncAppearanceControls();
        }
        QueueComposerLayout();
    }
    private void Comfortable_Click(object sender, RoutedEventArgs e)
    {
        ResizeTo(800, 850); ComposerExpander.IsExpanded = false; AppearancePanel.Visibility = Visibility.Collapsed;
        SyncAppearanceControls(); NotifyLayoutChanged();
    }
    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        ResizeTo(600, 740); Answer.FontSize = 18;
        themeId = Theme.InkId; accentHex = ""; customBase = ""; customInk = "";
        backgroundOpacity = 1; CommitTheme();
    }
    private void ThemePreset_Click(object sender, RoutedEventArgs e)
    {
        if (!appearanceReady || applyingAppearance || sender is not Button { Tag: string id }) return;
        themeId = id; accentHex = ""; customBase = ""; customInk = "";
        CommitTheme();
    }
    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (!appearanceReady || applyingAppearance || sender is not Button { Tag: string hex }) return;
        accentHex = hex; CommitTheme();
    }
    private void ApplyCustom_Click(object sender, RoutedEventArgs e)
    {
        if (!appearanceReady || applyingAppearance) return;
        var nextBase = CustomBaseBox.Text.Trim();
        var nextInk = CustomInkBox.Text.Trim();
        var nextAccent = CustomAccentBox.Text.Trim();
        if (!Theme.TryResolve(themeId, nextAccent, nextBase, nextInk, out _, out var error))
        {
            ThemeError.Text = error ?? "无法应用这种配色。"; return;
        }
        customBase = nextBase.Length == 0 ? "" : Theme.Canonical(nextBase);
        customInk = nextInk.Length == 0 ? "" : Theme.Canonical(nextInk);
        accentHex = nextAccent.Length == 0 ? "" : Theme.Canonical(nextAccent);
        ThemeError.Text = ""; CommitTheme();
    }
    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeTo(Width + e.HorizontalChange, Height + e.VerticalChange);
        SyncAppearanceControls(); NotifyLayoutChanged(); e.Handled = true;
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
            var editingDraft = DraftText.IsKeyboardFocusWithin && ComposerExpander.IsExpanded;
            var chrome = WorkArea.RowDefinitions[0].ActualHeight - ComposerScroll.ActualHeight
                + WorkArea.RowDefinitions[1].ActualHeight;
            // At the minimum window size, shrink the scrollable appearance pane first.
            // A focused draft takes priority over the answer's usual reserved space.
            var minimumEditor = Math.Clamp(DraftText.ActualHeight + DraftText.Margin.Top + DraftText.Margin.Bottom, 74, 110);
            var available = WorkArea.ActualHeight + (AppearancePanel.IsVisible ? AppearancePanel.ActualHeight : 0);
            AppearanceScroll.MaxHeight = editingDraft ? Math.Clamp(available - chrome - minimumEditor - 16, 30, 220) : 220;
            ComposerScroll.MaxHeight = editingDraft ? Math.Clamp(WorkArea.ActualHeight - chrome, minimumEditor, 220)
                : Math.Clamp(WorkArea.ActualHeight - 220, 0, 220);
            if (!editingDraft) return;
            // Scroll the existing caret into view; never focus or select the editor.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!DraftText.IsKeyboardFocusWithin) return;
                var caret = DraftText.GetRectFromCharacterIndex(DraftText.CaretIndex, true);
                if (!caret.IsEmpty) DraftText.BringIntoView(caret);
            }));
        }));
    }
}
