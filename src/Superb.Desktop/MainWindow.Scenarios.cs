using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Superb.Core;
using Superb.Core.Coding;

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
        ? key.Gesture + " · 独占" : key.Gesture + " · 普通组合键";
    private void PopulateSelectedScene()
    {
        var scene = ScenePicker.SelectedItem as ScenePreset;
        SceneName.Text = scene?.Name ?? "";
        SceneSystemPrompt.Text = scene?.SystemPrompt ?? "";
        SceneBackendPicker.ItemsSource = new[] { new BackendChoice("", "使用当前默认连接") }
            .Concat(config.Backends.Select(b => new BackendChoice(b.Id, b.Name))).ToArray();
        SceneBackendPicker.SelectedValue = scene?.BackendId ?? "";
        SceneTriggerSummary.Text = scene is null ? "请先新建场景" : "发起键：" + TriggerLabel(scene.Trigger);
        CodingAnalysisSettings.Visibility = scene?.Id == "coding" ? Visibility.Visible : Visibility.Collapsed;
        CodingAnalysisSettings.IsExpanded = false;
        CodingCalls.Text = config.CodingAnalysis.MaximumCalls?.ToString() ?? "";
        CodingSeconds.Text = config.CodingAnalysis.MaximumSeconds.ToString();
    }
    private void ScenePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading) PopulateSelectedScene(); }
    private void OpenHotkeys_Click(object sender, RoutedEventArgs e) { MainTabs.SelectedItem = ShortcutsTab; }
    private Task RunScene(ScenePreset scene)
    {
        if (!Ready()) return Task.CompletedTask;
        SelectWorkspaceScene(scene.Id);
        if (!reader.IsVisible) reader.Show();
        Status("场景已选择 · 使用当前输入与截图队列，准备好后发送");
        return Task.CompletedTask;
    }
    private void SaveScenarioSettings(Func<Configuration> build)
    {
        try
        {
            CancelProbe();
            var candidate = build(); candidate.Validate();
            if (!isolatedWorkspaceTest)
            {
                if (configLoadFailed && File.Exists(Configuration.FilePath))
                    File.Copy(Configuration.FilePath, Configuration.FilePath + ".invalid-" + Guid.NewGuid().ToString("N"), false);
                candidate.Save();
            }
            config = candidate; configLoadFailed = false;
            if (!isolatedWorkspaceTest) RegisterHotkeys();
            Populate(); Status("配置已保存；后续任务使用已保存的参数");
        }
        catch (Exception ex) { Report(ex); }
    }
    private void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenePicker.SelectedItem is not ScenePreset) return;
        SaveScenarioSettings(BuildSceneSettings);
    }
    internal Configuration BuildSceneSettings()
    {
        var scene = ScenePicker.SelectedItem as ScenePreset ?? throw new SafeFailure("scene_missing");
        var updated = scene with { Name = SceneName.Text.Trim(), SystemPrompt = SceneSystemPrompt.Text,
            BackendId = SceneBackendPicker.SelectedValue is string id && id.Length > 0 ? id : null };
        var analysis = config.CodingAnalysis;
        if (scene.Id == "coding")
        {
            int? calls = null;
            if (!string.IsNullOrWhiteSpace(CodingCalls.Text))
            {
                if (!int.TryParse(CodingCalls.Text, out var enteredCalls)) throw new SafeFailure("coding_budget_invalid");
                calls = enteredCalls;
            }
            if (!int.TryParse(CodingSeconds.Text, out var seconds))
                throw new SafeFailure("coding_budget_invalid");
            analysis = analysis with { MaximumCalls = calls, MaximumSeconds = seconds };
            analysis.Validate();
        }
        return config with { Scenes = config.Scenes.Replace(scene, updated), CodingAnalysis = analysis };
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
        SceneName.Text = original.Name; SceneSystemPrompt.Text = original.SystemPrompt;
        SceneBackendPicker.SelectedValue = "";
        if (scene.Id == "coding")
        {
            var defaults = new CodingRunOptions();
            CodingCalls.Text = defaults.MaximumCalls?.ToString() ?? "";
            CodingSeconds.Text = defaults.MaximumSeconds.ToString();
        }
        Status("已填入预设内容，保存场景后生效；发起键保持不变");
    }
    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        HotkeyRows.ItemsSource = config.AllHotkeys().Select(k => new HotkeyEditorRow(DefaultBinding(k.Action),
            config.Scenes.FirstOrDefault(s => s.Action == k.Action)?.Name is { } name ? "场景 · " + name : HotkeyDefaults.Label(k.Action), DefaultBinding(k.Action))).ToList();
        Status("已填入默认绑定，保存快捷键后生效");
    }
}
