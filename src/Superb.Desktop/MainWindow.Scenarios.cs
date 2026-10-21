using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Superb.Core;

namespace Superb.Desktop;

public partial class MainWindow
{
    private sealed record BackendChoice(string Id, string Label);
    private void PopulateScenesAndKeys()
    {
        var prior = (ScenePicker.SelectedItem as ScenePreset)?.Id;
        ScenePicker.ItemsSource = config.Scenes;
        ScenePicker.SelectedItem = config.Scenes.FirstOrDefault(s => s.Id == prior) ?? config.Scenes.FirstOrDefault();
        HotkeyRows.ItemsSource = config.AllHotkeys().Select(k => new HotkeyEditorRow(k,
            config.Scenes.FirstOrDefault(s => s.Action == k.Action)?.Name is { } name ? "场景 · " + name : HotkeyDefaults.Label(k.Action),
            DefaultBinding(k.Action))).ToList();
        PopulateSelectedScene();
    }
    private Hotkey DefaultBinding(string action) => HotkeyDefaults.All.FirstOrDefault(k => k.Action == action)
        ?? SceneDefaults.All.FirstOrDefault(s => s.Action == action)?.Trigger ?? new(action, "Ctrl+Alt+N", Enabled: false);
    private string TriggerLabel(Hotkey key) => !key.Enabled ? "快捷键关闭" : key.Mode == HotkeyMode.Exclusive
        ? key.Gesture + (config.ExclusiveHotkeysEnabled ? " · 独占" : " · 独占接管未开启") : key.Gesture + " · 普通组合键";
    private void PopulateSelectedScene()
    {
        var scene = ScenePicker.SelectedItem as ScenePreset;
        SceneName.Text = scene?.Name ?? "";
        SceneInputPicker.SelectedIndex = (int)(scene?.Input ?? SceneInput.Clipboard);
        SceneSystemPrompt.Text = scene?.SystemPrompt ?? "";
        SceneUserTemplate.Text = scene?.UserTemplate ?? "{input}";
        SceneModel.Text = scene?.Model ?? "";
        SceneTemperature.Text = scene?.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "";
        SceneTokens.Text = scene?.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
        SceneBackendPicker.ItemsSource = new[] { new BackendChoice("", "继承当前后端 / 固定路由") }
            .Concat(config.Backends.Select(b => new BackendChoice(b.Id, b.Name))).ToArray();
        SceneBackendPicker.SelectedValue = scene?.BackendId ?? "";
        SceneTriggerSummary.Text = scene is null ? "请先新建场景" : "发起键：" + TriggerLabel(scene.Trigger);
    }
    private void ScenePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading) PopulateSelectedScene(); }
    private void OpenHotkeys_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedItem = ShortcutsTab; }
    private async Task RunScene(ScenePreset scene)
    {
        if (!Ready()) return;
        if (Working) { Status("正在处理；请先停止生成"); return; }
        workspaceSceneId = scene.Id;
        RefreshWorkspace();
        StopSync(); // A manual scene must not race an already authorized clipboard auto-send.
        if (scene.Input == SceneInput.Question) await AskQuestion(scene);
        else await CaptureInput(scene.Input == SceneInput.Screen, scene: scene);
    }
    private void SaveScenarioSettings(Func<Configuration> build)
    {
        try
        {
            CancelProbe();
            var candidate = build(); candidate.Validate();
            if (configLoadFailed && File.Exists(Configuration.FilePath))
                File.Copy(Configuration.FilePath, Configuration.FilePath + ".invalid-" + Guid.NewGuid().ToString("N"), false);
            candidate.Save();
            config = candidate; configLoadFailed = false;
            RegisterHotkeys(); Populate(); Status("配置已保存；快捷键状态见“快捷键”页");
        }
        catch (Exception ex) { Report(ex); }
    }
    private static double ParseSceneTemperature(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new SafeFailure("scene_invalid");
    private static int ParseSceneTokens(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new SafeFailure("scene_invalid");
    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenePicker.SelectedItem is not ScenePreset scene) return;
        SaveScenarioSettings(() =>
        {
            var updated = scene with { Name = SceneName.Text.Trim(), Input = (SceneInput)SceneInputPicker.SelectedIndex,
                SystemPrompt = SceneSystemPrompt.Text, UserTemplate = SceneUserTemplate.Text,
                BackendId = SceneBackendPicker.SelectedValue is string id && id.Length > 0 ? id : null,
                Model = SceneModel.Text.Trim(), Temperature = string.IsNullOrWhiteSpace(SceneTemperature.Text) ? null : ParseSceneTemperature(SceneTemperature.Text),
                MaxTokens = string.IsNullOrWhiteSpace(SceneTokens.Text) ? null : ParseSceneTokens(SceneTokens.Text) };
            var candidate = config with { Scenes = config.Scenes.Replace(scene, updated) };
            return candidate;
        });
    }
    private void NewScene_Click(object sender, RoutedEventArgs e)
    {
        var scene = SceneDefaults.Custom();
        SaveScenarioSettings(() => config with { Scenes = config.Scenes.Add(scene) });
        ScenePicker.SelectedItem = config.Scenes.FirstOrDefault(s => s.Id == scene.Id);
    }
    private void RestoreMissingScenes_Click(object sender, RoutedEventArgs e) => SaveScenarioSettings(() =>
    {
        var missing = SceneDefaults.All.Where(p => !config.Scenes.Any(s => s.Id == p.Id));
        var candidate = config with { Scenes = config.Scenes.AddRange(missing) };
        return candidate;
    });
    private void DeleteScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenePicker.SelectedItem is not ScenePreset scene) return;
        SaveScenarioSettings(() => config with { Scenes = config.Scenes.Remove(scene) });
    }
    private void RestoreScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenePicker.SelectedItem is not ScenePreset scene) return;
        var original = SceneDefaults.All.FirstOrDefault(s => s.Id == scene.Id);
        if (original is null) { Status("自定义场景没有内置预设；可编辑后保存"); return; }
        // Fill the editor only; saving is explicit, and the user's binding remains unchanged.
        SceneName.Text = original.Name; SceneSystemPrompt.Text = original.SystemPrompt; SceneUserTemplate.Text = original.UserTemplate;
        SceneInputPicker.SelectedIndex = (int)original.Input; SceneBackendPicker.SelectedValue = "";
        SceneModel.Text = SceneTemperature.Text = SceneTokens.Text = "";
        Status("已填入预设内容，保存场景后生效；发起键保持不变");
    }
    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        HotkeyRows.ItemsSource = config.AllHotkeys().Select(k => new HotkeyEditorRow(DefaultBinding(k.Action),
            config.Scenes.FirstOrDefault(s => s.Action == k.Action)?.Name is { } name ? "场景 · " + name : HotkeyDefaults.Label(k.Action), DefaultBinding(k.Action))).ToList();
        ExclusiveReaderKeys.IsChecked = false;
        Status("已填入默认绑定，保存快捷键后生效");
    }
}
