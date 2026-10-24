using System.Windows;
using System.Windows.Controls;

namespace Superb.Desktop;

public partial class ReaderWindow
{
    private IReadOnlyDictionary<string, ShortcutLabel> shortcutLabels = new Dictionary<string, ShortcutLabel>();
    private bool copyingPartial, selectingCorners;
    internal void SetShortcutHints(IReadOnlyDictionary<string, ShortcutLabel> labels)
    {
        if (labels.Count == shortcutLabels.Count && labels.All(item => shortcutLabels.TryGetValue(item.Key, out var old) && old == item.Value)) return;
        shortcutLabels = labels;
        RefreshShortcutHints();
    }
    private ShortcutLabel KeyHint(string action) => shortcutLabels.TryGetValue(action, out var label) ? label : new("未注册", "本次会话尚未取得快捷键注册状态。");
    private void RefreshShortcutHints()
    {
        ShortcutHint.Apply(ThroughButton, KeyHint("through"));
        ShortcutHint.Apply(SettingsButton, KeyHint("settings")); ShortcutHint.Apply(HideButton, KeyHint("toggle"));
        ShortcutHint.Apply(SendButton, KeyHint("send"));
        ShortcutHint.Apply(ClipboardButton, KeyHint("clipboard")); ShortcutHint.Apply(CaptureButton, KeyHint("capture"));
        ShortcutHint.Apply(ScreenButton, KeyHint("captureScreen"));
        ShortcutHint.Apply(ScreenSendButton, KeyHint("captureScreenSend"));
        ShortcutHint.Apply(StopButton, selectingCorners ? new("Esc / 右键", "两点截图时按 Esc 或右键取消；" + KeyHint("cancel").Description, true) : KeyHint("cancel"));
        ShortcutHint.Apply(CopyButton, copyingPartial ? new("仅鼠标", "点击复制部分回答；全局复制键仅复制完整回答。") : KeyHint("copy"));
        ShortcutHint.Apply(RetryButton, KeyHint("retry"));
        ShortcutHint.Apply(PauseButton, KeyHint(ready ? "pause" : "resume"));
        RefreshInputHints();
    }
    private void RefreshInputHints()
    {
        if (InputQueue.ItemsSource is not IEnumerable<InputTile> source) return;
        var values = source.ToArray();
        for (var i = 0; i < values.Length; i++)
            values[i].SetRemoveHint(i == values.Length - 1 ? KeyHint("deleteCapture") : ShortcutLabel.Mouse);
    }
}
