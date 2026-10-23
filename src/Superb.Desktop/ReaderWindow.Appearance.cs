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
        WindowSurface.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(backgroundOpacity * 255), 16, 23, 22));
        AnswerSurface.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(backgroundOpacity * 255), 20, 31, 27));
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
        }
        finally { applyingAppearance = false; }
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
        ResizeTo(600, 740); Answer.FontSize = 18; SetBackgroundOpacity(1);
        SyncAppearanceControls(); NotifyLayoutChanged();
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
            AppearanceScroll.MaxHeight = editingDraft ? Math.Clamp(available - chrome - minimumEditor - 16, 30, 142) : 142;
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
