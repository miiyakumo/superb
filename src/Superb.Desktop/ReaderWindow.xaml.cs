using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool allowClose, populating, busy, ready, handlingProtectionLoss, canRetryAnswer;
    private int? directRetryLimit;
    private string throughKey = "Ctrl+Alt+E";
    internal bool ClickThrough { get; private set; }
    internal string? SelectedSceneId => WorkspaceScene.SelectedValue as string;
    public ReaderWindow()
    {
        InitializeComponent();
        protection = new(this, true);
        InitializeFocusPolicy();
        SourceInitialized += (_, _) =>
        {
            var ok = protection.Install();
            if (ok) { InitializePassThrough(); ok = displayLayer!.Check(); }
            ProtectionStatus.Text = ok ? "保护属性已设置 · 当前采集配置未实测" : "保护不可用 · 敏感显示已关闭";
            ProtectionStatus.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok) Clear();
        };
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; Command?.Invoke("toggle"); } };
        LocationChanged += (_, _) => Check();
        SizeChanged += (_, _) => Check();
        InitializeAppearance();
        UpdateControls();
    }
    public bool Check()
    {
        if (handlingProtectionLoss) return false;
        if (protection.Handle == 0) return false;
        if (protection.Check() && (displayLayer?.Check() ?? true)) return true;
        handlingProtectionLoss = true;
        try
        {
            displayLayer?.Invalidate();
            Clear(); ProtectionStatus.Text = "保护不可用 · 敏感显示已关闭"; ProtectionStatus.Visibility = Visibility.Visible;
            ProtectionLost?.Invoke(); return false;
        }
        finally { handlingProtectionLoss = false; }
    }
    public new void Show()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (!protection.RefreshDisplayAffinity()) { Clear(); ProtectionLost?.Invoke(); return; }
        SetClickThrough(false);
        Topmost = true;
        Native.SetWindowPos(protection.Handle, (nint)(-1), 0, 0, 0, 0, 0x0013);
        base.Show(); Check();
    }
    public new void Hide()
    {
        if (protection.Handle != 0 && protection.Check()) SetClickThrough(false);
        base.Hide();
        if (protection.Handle != 0 && !protection.RefreshDisplayAffinity()) { Clear(); ProtectionLost?.Invoke(); }
    }
    internal bool SetClickThrough(bool through)
    {
        if (!protection.SetInteraction(false, through)) { Clear(); ProtectionLost?.Invoke(); return false; }
        ClickThrough = through; UpdateControls();
        return true;
    }
    private void UpdateControls()
    {
        WorkspaceScene.IsEnabled = ready && (!busy || codingMode);
        ModeHint.Text = ClickThrough ? $"全穿透 · {throughKey} 恢复操作" : "";
        ModeHint.Visibility = ClickThrough ? Visibility.Visible : Visibility.Collapsed;
        RefreshShortcutHints();
        RefreshInputRegions();
    }
    internal void Configure(IEnumerable<ScenePreset> scenes, string? selectedId, string receiver, string recoveryKey)
    {
        populating = true;
        var next = scenes.ToArray();
        if (WorkspaceScene.ItemsSource is not ScenePreset[] existing || !existing.SequenceEqual(next)) WorkspaceScene.ItemsSource = next;
        if (!Equals(WorkspaceScene.SelectedValue, selectedId)) WorkspaceScene.SelectedValue = selectedId;
        ReceiverText.Text = receiver; throughKey = recoveryKey; populating = false; UpdateControls();
    }
    internal void SetActions(bool ready, bool working, bool hasInput, bool canCopy, bool partial, bool canRetry)
    {
        this.ready = ready;
        copyingPartial = partial;
        busy = working;
        SendButton.IsEnabled = ready && !working && hasInput;
        ClipboardButton.IsEnabled = CaptureButton.IsEnabled = ScreenButton.IsEnabled = ScreenSendButton.IsEnabled = ready && !working;
        StopButton.IsEnabled = ready && working;
        CopyButton.IsEnabled = ready && canCopy;
        CopyButton.Content = partial ? "复制部分回答" : "复制回答";
        canRetryAnswer = canRetry;
        RetryButton.IsEnabled = ready && !working && canRetry;
        StopButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = canCopy && !working ? Visibility.Visible : Visibility.Collapsed;
        RefreshRetryVisibility();
        DirectRetryButton.IsEnabled = ready && !working;
        ThroughButton.IsEnabled = ready;
        InputQueue.IsEnabled = ClearInputButton.IsEnabled = ready && !working;
        PauseButton.Content = ready ? "暂停" : "恢复";
        UpdateControls();
    }
    public void Clear()
    {
        ClearCodingProgress();
        CodingHistory.Text = ""; CodingPending.Text = "";
        ClearInput(); ClearAnswer(); StateText.Text = "等待输入";
        ComposerExpander.IsExpanded = true;
    }
    internal void ClearAnswer()
    {
        answerMode = false; UpdateControls(); Answer.Clear(); EmptyAnswer.Text = "选择场景，读取剪贴板或收集截图。\n确认队列后发送。";
        EmptyAnswer.Visibility = Visibility.Visible;
    }
    internal void ClearInput()
    {
        inputIds = []; InputQueue.ItemsSource = null; QueueCount.Text = ""; Attachment.Visibility = Visibility.Collapsed;
        InputHeading.Text = "材料队列";
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
        InputHeading.Text = values.Length == 0 ? "材料队列" : $"材料队列 · {values.Length} 项";
        Attachment.Visibility = values.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void SetSelecting(bool selecting)
    {
        selectingCorners = selecting; StopButton.Content = selecting ? "取消截图" : codingMode ? "停止分析" : "停止生成";
        RefreshShortcutHints();
    }
    internal void SetDirectRetry(int? limit)
    {
        directRetryLimit = limit;
        RefreshRetryVisibility();
        if (limit is not { } tokens) return;
        DirectRetryButton.Content = $"直接回答（{(tokens == 16384 ? "16K" : tokens.ToString())} tokens）";
        DirectRetryButton.ToolTip = $"仅这次重试关闭 DeepSeek 深度思考，生成上限改为 {tokens} tokens，可能增加用量；保持上次输入、模型和保护规则，不修改保存的设置。";
    }
    private void RefreshRetryVisibility()
    {
        RetryButton.Visibility = !busy && canRetryAnswer && directRetryLimit is null ? Visibility.Visible : Visibility.Collapsed;
        DirectRetryButton.Visibility = !busy && directRetryLimit is not null ? Visibility.Visible : Visibility.Collapsed;
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
    internal PanelLayout Layout() => new(Left, Top, Width, Height, Answer.FontSize, IsVisible, backgroundOpacity, themeId, accentHex, customBase, customInk);
    internal void ApplyLayout(PanelLayout layout)
    {
        applyingAppearance = true;
        try
        {
            Left = Math.Clamp(layout.Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - MinWidth));
            Top = Math.Clamp(layout.Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - MinHeight));
            ResizeTo(layout.Width, layout.Height);
            Answer.FontSize = Math.Clamp(layout.FontSize, 11, 36);
            themeId = string.IsNullOrWhiteSpace(layout.Theme) ? Theme.InkId : layout.Theme;
            accentHex = layout.Accent ?? "";
            customBase = layout.CustomBase ?? "";
            customInk = layout.CustomInk ?? "";
            backgroundOpacity = layout.BackgroundOpacity;
            ApplyPalette();
        }
        finally { applyingAppearance = false; }
    }
    public void Finish() { allowClose = true; Close(); }
    private void WorkspaceScene_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!populating) Command?.Invoke("selectScene"); }
    private void Through_Click(object sender, RoutedEventArgs e) => Command?.Invoke("through");
    private void Hide_Click(object sender, RoutedEventArgs e) => Command?.Invoke("toggle");
    private void Send_Click(object sender, RoutedEventArgs e) => Command?.Invoke("send");
    private void Clipboard_Click(object sender, RoutedEventArgs e) => Command?.Invoke("stageClipboard");
    private void Capture_Click(object sender, RoutedEventArgs e) => Command?.Invoke("stageCapture");
    private void Screen_Click(object sender, RoutedEventArgs e) => Command?.Invoke("captureScreen");
    private void ScreenSend_Click(object sender, RoutedEventArgs e) => Command?.Invoke("captureScreenSend");
    private void RemoveInput_Click(object sender, RoutedEventArgs e) => Command?.Invoke(sender is Button { Tag: Guid id } ? "removeInput:" + id : "removeInput");
    private void Stop_Click(object sender, RoutedEventArgs e) => Command?.Invoke("cancel");
    private void NewTask_Click(object sender, RoutedEventArgs e) => Command?.Invoke("newTask");
    private void Retry_Click(object sender, RoutedEventArgs e) => Command?.Invoke("retry");
    private void Copy_Click(object sender, RoutedEventArgs e) => Command?.Invoke("workspaceCopy");
    private void Pause_Click(object sender, RoutedEventArgs e) => Command?.Invoke(Equals(PauseButton.Content, "恢复") ? "resume" : "pause");
    private void Settings_Click(object sender, RoutedEventArgs e) => Command?.Invoke("settings");
}
