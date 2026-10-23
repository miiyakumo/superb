using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Superb.Desktop;

// Caption hints are display-only: never register keys or replace the button's existing action.
public static class ShortcutHint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(ShortcutHint), new PropertyMetadata("仅鼠标"));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.RegisterAttached("Description", typeof(string), typeof(ShortcutHint), new PropertyMetadata("此操作未设置专用快捷键。"));
    private static readonly DependencyProperty ActionHintProperty = DependencyProperty.RegisterAttached("ActionHint", typeof(string), typeof(ShortcutHint));
    private static readonly DependencyProperty ComposedToolTipProperty = DependencyProperty.RegisterAttached("ComposedToolTip", typeof(string), typeof(ShortcutHint));
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetDescription(DependencyObject target) => (string)target.GetValue(DescriptionProperty);
    public static void SetDescription(DependencyObject target, string value) => target.SetValue(DescriptionProperty, value);
    internal static void Apply(Button button, ShortcutLabel label)
    {
        // Shortcuts stay in the tooltip. A later call must not append the previous shortcut onto itself,
        // but an explicit tooltip written by the caller (such as the edit-mode notice) still replaces it.
        var live = button.ToolTip as string ?? "";
        var composed = button.ReadLocalValue(ComposedToolTipProperty) as string;
        var actionHint = button.ReadLocalValue(ActionHintProperty) is string stored && live == composed ? stored : live;
        button.SetValue(ActionHintProperty, actionHint);
        var description = actionHint.Length > 0 ? actionHint + " " + label.Description : label.Description;
        SetText(button, label.Text); SetDescription(button, description);
        AutomationProperties.SetHelpText(button, description);
        button.SetValue(ComposedToolTipProperty, description);
        button.ToolTip = description;
    }
}

internal sealed record ShortcutLabel(string Text, string Description, bool Active = false)
{
    internal static ShortcutLabel Mouse { get; } = new("仅鼠标", "此操作未设置专用快捷键。");
    internal static ShortcutLabel Describe(Hotkey? binding, bool exclusiveEnabled, bool registered, string? error = null)
    {
        if (binding is null) return new("未设置", "此动作未配置全局快捷键。");
        if (!binding.Enabled) return new("已停用", $"已配置 {binding.Gesture}，当前已停用。");
        if (binding.Mode == HotkeyMode.Exclusive && !exclusiveEnabled) return new("接管未开", $"已配置独占单键 {binding.Gesture}，独占接管尚未开启。");
        if (registered) return new(binding.Gesture, $"当前全局快捷键：{binding.Gesture}。" + (binding.Mode == HotkeyMode.Exclusive ? "此单键由 Superb 独占接管。" : ""), true);
        return error is null ? new("未注册", $"已配置 {binding.Gesture}，本次会话尚未注册成功。")
            : new("未生效", $"已配置 {binding.Gesture}，注册失败：{error}。请在设置中检查快捷键冲突。");
    }
}
