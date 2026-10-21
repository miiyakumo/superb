using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Superb.Core;

namespace Superb.Desktop;

internal enum HotkeyMode { Combination, Exclusive }
internal enum SceneInput { Clipboard, Question, Screen }
internal sealed record Hotkey(string Action, string Gesture, HotkeyMode Mode = HotkeyMode.Combination, bool Enabled = true);
internal sealed record ScenePreset(string Id, string Name, string SystemPrompt, string UserTemplate, SceneInput Input,
    Hotkey Trigger, string? BackendId = null, string Model = "", double? Temperature = null, int? MaxTokens = null)
{
    public string Action => "scene:" + Id;
}

internal static class SceneDefaults
{
    public static ImmutableArray<ScenePreset> All =>
    [new("coding", "AI Coding 协作", "你是一位严谨的编程协作助手。先理解目标、现有代码和约束，区分事实与假设；给出可审阅的修改方案和必要代码，并说明验证方式。信息不足时明确指出。不要声称已经执行代码或修改文件。", "请协助处理以下开发任务：\n\n{input}", SceneInput.Clipboard, new("scene:coding", "Ctrl+Alt+C")),
     new("algorithm", "算法题", "你是一位算法讲解助手。分析题意、输入输出与约束，推导解法，说明正确性、时间和空间复杂度，给出代码及边界测试。语言未指定时使用 Python。缺少约束时说明假设。", "请分析并解答以下算法题：\n\n{input}", SceneInput.Clipboard, new("scene:algorithm", "Ctrl+Alt+A")),
     new("general", "通用问答", "准确、简洁地回答问题。区分已知事实与推测，必要时说明不确定性。", "{input}", SceneInput.Clipboard, new("scene:general", "Ctrl+Alt+G"))];
    public static ScenePreset Custom() { var id = Guid.NewGuid().ToString("N"); return new(id, "新场景", "准确、简洁地回答问题。", "{input}", SceneInput.Question, new("scene:" + id, "Ctrl+Alt+N", Enabled: false)); }
}

internal static class HotkeyDefaults
{
    public static ImmutableArray<Hotkey> All =>
    [new("clipboard", "Ctrl+Alt+1"), new("capture", "Ctrl+Alt+2"), new("toggle", "Ctrl+Alt+3"),
     new("copy", "Ctrl+Alt+4"), new("cancel", "Ctrl+Alt+5"), new("pause", "Ctrl+Alt+6"),
     new("send", "Ctrl+Alt+7"), new("pageDown", "Ctrl+Alt+8"), new("pageUp", "Ctrl+Alt+9"),
     new("settings", "Ctrl+Alt+0"), new("exit", "Ctrl+Alt+Q"), new("ask", "Ctrl+Alt+Enter"),
     new("retry", "Ctrl+Alt+R"), new("resume", "Ctrl+Alt+S")];
    public static string Label(string action) => action switch
    {
        "clipboard" => "问剪贴板", "capture" => "截图提问", "toggle" => "显示 / 收起浮窗", "copy" => "复制回答",
        "cancel" => "取消请求", "pause" => "紧急暂停", "send" => "发送已准备内容", "pageDown" => "向下翻页",
        "pageUp" => "向上翻页", "settings" => "打开设置", "exit" => "退出", "ask" => "发送输入框问题", "retry" => "重新回答", "resume" => "恢复使用", _ => action
    };
    public static string Exclusive(string action) => action switch
    {
        "toggle" => "F6", "pageDown" => "F7", "pageUp" => "F8", "scene:coding" => "F9", "scene:algorithm" => "F10", "scene:general" => "F11",
        "clipboard" => "F1", "capture" => "F2", "copy" => "F3", "cancel" => "F4", "pause" => "F5", "send" => "F13",
        "settings" => "F14", "exit" => "F15", "ask" => "F16", "retry" => "F17", "resume" => "F18", _ => "F19"
    };
    public static KeyGesture Parse(Hotkey binding)
    {
        if (!Enum.IsDefined(binding.Mode) || string.IsNullOrWhiteSpace(binding.Action) || string.IsNullOrWhiteSpace(binding.Gesture) || binding.Gesture.Length > 80)
            throw new SafeFailure("hotkey_invalid");
        KeyGesture gesture;
        try { gesture = (KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(binding.Gesture)!; }
        catch { throw new SafeFailure("hotkey_invalid"); }
        if (gesture is null) throw new SafeFailure("hotkey_invalid");
        if (!binding.Enabled) return gesture;
        if (gesture.Key == Key.F12 || gesture.Modifiers.HasFlag(ModifierKeys.Windows)) throw new SafeFailure("hotkey_reserved");
        if (binding.Mode == HotkeyMode.Exclusive)
        {
            if (gesture.Modifiers != ModifierKeys.None || !(gesture.Key is >= Key.F1 and <= Key.F11 or >= Key.F13 and <= Key.F24))
                throw new SafeFailure("hotkey_exclusive_function_required");
        }
        else if (gesture.Modifiers == ModifierKeys.None ||
            (gesture.Key == Key.Delete && gesture.Modifiers.HasFlag(ModifierKeys.Control) && gesture.Modifiers.HasFlag(ModifierKeys.Alt)) ||
            (gesture.Key == Key.Tab && gesture.Modifiers.HasFlag(ModifierKeys.Alt)) ||
            (gesture.Key == Key.F4 && gesture.Modifiers.HasFlag(ModifierKeys.Alt)) ||
            (gesture.Key == Key.Escape && gesture.Modifiers.HasFlag(ModifierKeys.Control))) throw new SafeFailure("hotkey_combination_required");
        return gesture;
    }
    public static void Validate(IEnumerable<Hotkey> bindings)
    {
        var actions = new HashSet<string>(); var gestures = new HashSet<(Key, ModifierKeys)>();
        foreach (var binding in bindings)
        {
            if (binding is null || !actions.Add(binding.Action)) throw new SafeFailure("hotkey_action_duplicate");
            var gesture = Parse(binding);
            if (binding.Enabled && !gestures.Add((gesture.Key, gesture.Modifiers))) throw new SafeFailure("hotkey_duplicate");
        }
    }
}

internal sealed class HotkeyEditorRow : INotifyPropertyChanged
{
    private string gesture;
    private HotkeyMode mode;
    public string Action { get; }
    public string Label { get; }
    public bool Enabled { get; set; }
    public string Gesture { get => gesture; set { gesture = value; Changed(); } }
    public HotkeyMode Mode { get => mode; set { if (mode == value) return; mode = value; Gesture = value == HotkeyMode.Exclusive ? HotkeyDefaults.Exclusive(Action) : Default.Gesture; Changed(); } }
    public Hotkey Default { get; }
    public static object[] Modes { get; } = [new { Value = HotkeyMode.Combination, Label = "普通组合键" }, new { Value = HotkeyMode.Exclusive, Label = "独占单键" }];
    public HotkeyEditorRow(Hotkey binding, string label, Hotkey fallback) { Action = binding.Action; Label = label; Default = fallback; gesture = binding.Gesture; mode = binding.Mode; Enabled = binding.Enabled; }
    public Hotkey Binding() => new(Action, Gesture.Trim(), Mode, Enabled);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

internal static class SceneRequestFactory
{
    public static PreparedRequest Prepare(Configuration settings, ScenePreset scene, InputSnapshot snapshot, IImageProcessor images)
    {
        // Mandatory global transformations still apply before the scene template, including masks.
        if (snapshot.Content.Size > Policy.AbsoluteInputLimit) throw new SafeFailure("input_too_large");
        if (string.IsNullOrWhiteSpace(snapshot.Content.Text) && !snapshot.Content.HasImage) throw new SafeFailure("input_empty");
        var notices = new List<string>();
        var processed = Preprocessor.Process(snapshot.Content, settings.Pipeline, images, notices.Add);
        var target = scene.BackendId is null ? settings.Routing.Select(processed, settings.SelectedBackendId, settings.Backends)
            : settings.Backends.FirstOrDefault(b => b.Id == scene.BackendId) ?? throw new SafeFailure("scene_backend_missing");
        target = target with { Model = string.IsNullOrWhiteSpace(scene.Model) ? target.Model : scene.Model,
            Temperature = scene.Temperature ?? target.Temperature, MaxTokens = scene.MaxTokens ?? target.MaxTokens };
        processed = processed with { Text = scene.UserTemplate.Replace("{input}", processed.Text ?? "", StringComparison.Ordinal) };
        var request = new PreparedRequest(snapshot with { Content = processed }, processed, settings.Pipeline, target,
            notices.ToImmutableArray(), scene.SystemPrompt, scene.Id);
        Policy.Check(request);
        return request;
    }
}
