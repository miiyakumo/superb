using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class ReaderWindow : Window
{
    private sealed record InputTile(Guid Id, string Label, string Summary, BitmapSource? Thumbnail) : INotifyPropertyChanged
    {
        public Visibility ImageVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        private ShortcutLabel removeHint = ShortcutLabel.Mouse;
        public string RemoveHint => removeHint.Text;
        public string RemoveDescription => removeHint.Description;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void SetRemoveHint(ShortcutLabel value)
        {
            if (removeHint == value) return;
            removeHint = value; PropertyChanged?.Invoke(this, new(nameof(RemoveHint))); PropertyChanged?.Invoke(this, new(nameof(RemoveDescription)));
        }
    }
    private Guid[] inputIds = [];
    private readonly Protection protection;
    public event Action<string>? Command;
    public event Action? ProtectionLost;
    internal event Action? DraftChanged;
    private bool allowClose, populating, busy, ready;
    private nint previousForeground;
    private string interactionKey = "Ctrl+Alt+E";
    internal bool Interactive { get; private set; }
    internal bool ClickThrough { get; private set; }
    internal string Draft => DraftText.Text;
    internal string? SelectedSceneId => WorkspaceScene.SelectedValue as string;
    public ReaderWindow()
    {
        InitializeComponent();
        protection = new(this, true);
        SourceInitialized += (_, _) =>
        {
            var ok = protection.Install();
            ProtectionStatus.Text = ok ? "保护属性已设置 · 当前采集配置未实测" : "保护不可用 · 敏感显示已关闭";
            if (!ok) Clear();
        };
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; Command?.Invoke("toggle"); } };
        LocationChanged += (_, _) => Check();
        SizeChanged += (_, _) => Check();
        InitializeAppearance();
        SetEditingControls();
    }
    public bool Check()
    {
        if (protection.Handle == 0) return false;
        if (protection.Check()) return true;
        Clear(); ProtectionStatus.Text = "保护不可用 · 敏感显示已关闭";
        ProtectionLost?.Invoke(); return false;
    }
    public new void Show()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (!protection.RefreshDisplayAffinity()) { Clear(); ProtectionLost?.Invoke(); return; }
        SetMode(false);
        Topmost = true;
        Native.SetWindowPos(protection.Handle, (nint)(-1), 0, 0, 0, 0, 0x0013);
        base.Show(); Check();
    }
    public new void Hide()
    {
        if (protection.Handle != 0 && protection.Check()) SetMode(false);
        base.Hide();
        if (protection.Handle != 0 && !protection.RefreshDisplayAffinity()) { Clear(); ProtectionLost?.Invoke(); }
    }
    internal bool SetMode(bool interactive, bool through = false, bool focusDraft = true)
    {
        // Capture the originating app before changing native window styles.
        if (interactive && !Interactive)
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground != protection.Handle) previousForeground = foreground;
        }
        if (!protection.SetInteraction(interactive, through)) { Clear(); ProtectionLost?.Invoke(); return false; }
        var wasInteractive = Interactive;
        Interactive = interactive; ClickThrough = through; SetEditingControls();
        if (interactive) { Activate(); Native.SetForegroundWindow(protection.Handle); if (focusDraft && !busy) DraftText.Focus(); }
        else if (wasInteractive)
        {
            Keyboard.ClearFocus();
            if (previousForeground != 0 && Native.IsWindow(previousForeground)) Native.SetForegroundWindow(previousForeground);
            previousForeground = 0;
        }
        return true;
    }
    private void SetEditingControls()
    {
        DraftText.IsReadOnly = !ready || !Interactive || busy;
        // Explicit clicks enter editing; simply showing or updating the window never activates it.
        DraftText.Focusable = ready && !busy;
        WorkspaceScene.IsEnabled = ready && !busy;
        Answer.SetInteractive(Interactive);
        InteractionButton.Content = Interactive ? "完成编辑" : "编辑";
        ModeHint.Text = ClickThrough ? $"穿透阅读 · {interactionKey} 返回编辑" : Interactive
            ? "交互模式 · Ctrl+Enter 发送 · Esc 返回阅读" : "阅读模式 · 点击输入框或场景即可编辑";
        ModeHint.Visibility = answerMode && !Interactive && !ClickThrough ? Visibility.Collapsed : Visibility.Visible;
        RefreshShortcutHints();
    }
    internal void Configure(IEnumerable<ScenePreset> scenes, string? selectedId, string receiver, string editKey)
    {
        populating = true;
        var next = scenes.ToArray();
        if (WorkspaceScene.ItemsSource is not ScenePreset[] existing || !existing.SequenceEqual(next)) WorkspaceScene.ItemsSource = next;
        if (!Equals(WorkspaceScene.SelectedValue, selectedId)) WorkspaceScene.SelectedValue = selectedId;
        ReceiverText.Text = receiver; interactionKey = editKey; populating = false; SetEditingControls();
    }
    internal void SetActions(bool ready, bool working, bool hasInput, bool canCopy, bool partial, bool canRetry)
    {
        this.ready = ready;
        copyingPartial = partial;
        busy = working;
        SendButton.IsEnabled = ready && !working && hasInput;
        ClipboardButton.IsEnabled = CaptureButton.IsEnabled = ScreenButton.IsEnabled = ready && !working;
        StopButton.IsEnabled = ready && working;
        CopyButton.IsEnabled = ready && canCopy;
        CopyButton.Content = partial ? "复制部分回答" : "复制回答";
        RetryButton.IsEnabled = ready && !working && canRetry;
        DirectRetryButton.IsEnabled = ready && !working;
        ThroughButton.IsEnabled = ready;
        InputQueue.IsEnabled = ClearInputButton.IsEnabled = ready && !working;
        PauseButton.Content = ready ? "暂停" : "恢复";
        SetEditingControls();
    }
    public void Clear()
    {
        populating = true; DraftText.Clear(); populating = false;
        ClearInput(); ClearAnswer(); StateText.Text = "等待输入";
        ComposerExpander.IsExpanded = true;
    }
    internal void SetDraft(string text)
    {
        populating = true; DraftText.Text = text; populating = false;
    }
    internal void ClearAnswer()
    {
        answerMode = false; SetEditingControls(); Answer.Clear(); EmptyAnswer.Text = "选择场景，输入问题或收集截图、剪贴板。\n确认队列后发送。";
        EmptyAnswer.Visibility = Visibility.Visible;
    }
    internal void ClearInput()
    {
        inputIds = []; InputQueue.ItemsSource = null; QueueCount.Text = ""; Attachment.Visibility = Visibility.Collapsed;
        InputHeading.Text = "输入与队列";
    }
    public void Status(string text) => StateText.Text = text;
    public void Preview(Payload payload) => SetInput(payload, "已准备输入");
    internal void SetInput(Payload payload, string label)
    {
        SetInputs([new(new(Guid.NewGuid(), InputKind.Screen, payload, 0, DateTimeOffset.UtcNow), label)]);
    }
    internal void SetInputs(IEnumerable<WorkspaceAttachment> entries)
    {
        if (!Check()) return;
        var values = entries.ToArray();
        var ids = values.Select(i => i.Snapshot.Id).ToArray();
        if (inputIds.SequenceEqual(ids)) return;
        if (ids.Length > inputIds.Length) ComposerExpander.IsExpanded = true;
        inputIds = ids;
        InputQueue.ItemsSource = values.Select((entry, index) =>
        {
            var p = entry.Snapshot.Content;
            var preview = p.Text is { Length: > 90 } text ? text[..90] + "…" : p.Text;
            return new InputTile(entry.Snapshot.Id, $"{index + 1} · {entry.Label}",
                p.HasImage ? $"{p.Width} × {p.Height}" : $"{p.Text?.Length ?? 0} 字 · {preview}",
                p.HasImage ? ImageProcessor.Decode(p.Png) : null);
        }).ToArray();
        RefreshInputHints();
        QueueCount.Text = $"待发送队列 · {values.Length} 项";
        InputHeading.Text = values.Length == 0 ? "输入与队列" : $"输入与队列 · {values.Length} 项";
        Attachment.Visibility = values.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void SetSelecting(bool selecting)
    {
        selectingCorners = selecting; StopButton.Content = selecting ? "取消截图" : "停止";
        RefreshShortcutHints();
    }
    internal void SetDirectRetry(int? limit)
    {
        DirectRetryButton.Visibility = limit is null ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = limit is null ? Visibility.Visible : Visibility.Collapsed;
        if (limit is not { } tokens) return;
        DirectRetryButton.Content = $"直接回答（{(tokens == 16384 ? "16K" : tokens.ToString())} tokens）";
        DirectRetryButton.ToolTip = $"仅这次重试关闭 DeepSeek 深度思考，生成上限改为 {tokens} tokens，可能增加用量；保持上次输入、模型和保护规则，不修改保存的设置。";
    }
    private void DirectRetry_Click(object sender, RoutedEventArgs e) => Command?.Invoke("retryDirect");
    public void Render(string text, string state)
    {
        if (!Check()) return;
        if (text.Length > 0) BeginAnswer();
        StateText.Text = state; Answer.Render(text);
        if (text.Length == 0) EmptyAnswer.Text = state;
        EmptyAnswer.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public void Page(bool down) => Answer.Page(down);
    internal PanelLayout Layout() => new(Left, Top, Width, Height, Answer.FontSize, IsVisible, backgroundOpacity);
    internal void ApplyLayout(PanelLayout layout)
    {
        applyingAppearance = true;
        try
        {
            Left = Math.Clamp(layout.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - MinWidth));
            Top = Math.Clamp(layout.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - MinHeight));
            ResizeTo(layout.Width, layout.Height);
            Answer.FontSize = Math.Clamp(layout.FontSize, 11, 36);
            SetBackgroundOpacity(layout.BackgroundOpacity);
        }
        finally { applyingAppearance = false; }
        SyncAppearanceControls();
    }
    public void Finish() { allowClose = true; Close(); }
    private void WorkspaceScene_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!populating) Command?.Invoke("selectScene"); }
    private void DraftText_TextChanged(object sender, TextChangedEventArgs e) { if (!populating) DraftChanged?.Invoke(); }
    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ready && !busy && !Interactive) Command?.Invoke("beginEdit");
        // Let the same mouse event place the caret or open the scene picker normally.
    }
    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock && e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Interactive) { SetMode(false); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Command?.Invoke("workspaceSend"); e.Handled = true; }
    }
    private void Interaction_Click(object sender, RoutedEventArgs e) => Command?.Invoke("interact");
    private void Through_Click(object sender, RoutedEventArgs e) => Command?.Invoke("through");
    private void Hide_Click(object sender, RoutedEventArgs e) => Command?.Invoke("toggle");
    private void Send_Click(object sender, RoutedEventArgs e) => Command?.Invoke("workspaceSend");
    private void Clipboard_Click(object sender, RoutedEventArgs e) => Command?.Invoke("stageClipboard");
    private void Capture_Click(object sender, RoutedEventArgs e) => Command?.Invoke("stageCapture");
    private void Screen_Click(object sender, RoutedEventArgs e) => Command?.Invoke("captureScreen");
    private void RemoveInput_Click(object sender, RoutedEventArgs e) => Command?.Invoke(sender is Button { Tag: Guid id } ? "removeInput:" + id : "removeInput");
    private void Stop_Click(object sender, RoutedEventArgs e) => Command?.Invoke("cancel");
    private void NewTask_Click(object sender, RoutedEventArgs e) => Command?.Invoke("newTask");
    private void Retry_Click(object sender, RoutedEventArgs e) => Command?.Invoke("retry");
    private void Copy_Click(object sender, RoutedEventArgs e) => Command?.Invoke("workspaceCopy");
    private void Pause_Click(object sender, RoutedEventArgs e) => Command?.Invoke(Equals(PauseButton.Content, "恢复") ? "resume" : "pause");
    private void Settings_Click(object sender, RoutedEventArgs e) => Command?.Invoke("settings");
}
