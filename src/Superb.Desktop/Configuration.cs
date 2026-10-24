using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Superb.Core;
using Superb.Core.Coding;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

internal sealed record PanelLayout(double Left = 80, double Top = 80, double Width = 600, double Height = 740, double FontSize = 18, bool Visible = false, double BackgroundOpacity = 1, string Theme = Theme.InkId, string Accent = "", string CustomBase = "", string CustomInk = "");
internal sealed record Configuration
{
    public int SchemaVersion { get; init; } = 6;
    public ImmutableArray<Backend> Backends { get; init; } = [];
    public string SelectedBackendId { get; init; } = "";
    public bool AutoCopy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExclusiveReaderKeys { get; init; }
    public bool ClearOwnedClipboardOnPause { get; init; } = true;
    public PanelLayout Panel { get; init; } = new();
    public CodingRunOptions CodingAnalysis { get; init; } = new();
    public int CodingAnalysisVersion { get; init; } = 2;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExclusiveHotkeysEnabled { get; init; }
    public ImmutableArray<ScenePreset> Scenes { get; init; } = SceneDefaults.All;
    public ImmutableArray<Hotkey> Hotkeys { get; init; } = HotkeyDefaults.All;
    public IEnumerable<Hotkey> AllHotkeys() => Hotkeys.Concat(Scenes.Select(s => s.Trigger));
    internal Configuration WithoutBackend(string id)
    {
        if (!Backends.Any(b => b.Id == id)) throw new SafeFailure("backend_missing");
        if (Scenes.Any(s => s.BackendId == id)) throw new SafeFailure("scene_backend_in_use");
        var remaining = Backends.Where(b => b.Id != id).ToImmutableArray();
        return this with
        {
            Backends = remaining,
            SelectedBackendId = SelectedBackendId == id ? remaining.FirstOrDefault()?.Id ?? "" : SelectedBackendId
        };
    }
    public Configuration Migrate()
    {
        if (SchemaVersion is not (1 or 2 or 3 or 4 or 5 or 6)) throw new SafeFailure("config_invalid");
        if (Hotkeys.IsDefault || Scenes.IsDefault || Backends.IsDefault || Scenes.Any(s => s is null)) throw new SafeFailure("config_invalid");
        var legacy = SchemaVersion == 1;
        var keys = Hotkeys;
        if (keys.Any(k => k is null)) throw new SafeFailure("hotkey_invalid");
        if (keys.Count(k => k.Action == "ask") > 1 || keys.Count(k => k.Action == "send") > 1)
            throw new SafeFailure("hotkey_action_duplicate");
        // Upgrade only the previous stock combination. Reserve existing user keys first,
        // and preserve a disabled screenshot binding instead of silently enabling it.
        Hotkey? screenSendUpgrade = null;
        if (keys.FirstOrDefault(k => k.Action == "captureScreenSend") is { Mode: HotkeyMode.Combination, Gesture: "Ctrl+Alt+X" } oldScreenSend)
        {
            screenSendUpgrade = new("captureScreenSend", "F4", HotkeyMode.Exclusive, oldScreenSend.Enabled);
            keys = keys.Remove(oldScreenSend);
        }
        // Keep a customized send binding (including its disabled state). Otherwise prefer
        // the enabled former workspace shortcut. The backup retains the discarded alias.
        var send = keys.FirstOrDefault(k => k.Action == "send");
        var ask = keys.FirstOrDefault(k => k.Action == "ask");
        if (ask is not null)
        {
            var binding = send is null || (send == new Hotkey("send", "Ctrl+Alt+7") && ask.Enabled) ? ask : send;
            keys = keys.Where(k => k.Action is not ("ask" or "send")).Append(binding with { Action = "send" }).ToImmutableArray();
        }
        var oldInteraction = keys.FirstOrDefault(k => k.Action == "interact");
        if (oldInteraction is not null)
        {
            keys = keys.Where(k => k.Action != "interact").ToImmutableArray();
            if (!keys.Any(k => k.Action == "through")) keys = keys.Add(oldInteraction with { Action = "through" });
        }
        var used = new HashSet<(System.Windows.Input.Key, System.Windows.Input.ModifierKeys)>();
        Hotkey Reserve(Hotkey key)
        {
            var gesture = HotkeyDefaults.Parse(key);
            return key.Enabled && !used.Add((gesture.Key, gesture.Modifiers)) ? key with { Enabled = false } : key;
        }
        if (ExclusiveReaderKeys)
            keys = keys.Select(k => k.Action is "toggle" or "pageDown" or "pageUp" ? k with { Mode = HotkeyMode.Exclusive, Gesture = HotkeyDefaults.Exclusive(k.Action) } : k).ToImmutableArray();
        if (legacy) keys = keys.Select(key =>
        {
            try { return Reserve(key); }
            catch (SafeFailure)
            {
                // Retain a syntactically valid old binding for correction, but do not newly take it over.
                var gesture = HotkeyDefaults.Parse(key with { Enabled = false });
                var disabled = key with { Enabled = false, Mode = gesture.Modifiers == System.Windows.Input.ModifierKeys.None ? HotkeyMode.Exclusive : key.Mode };
                return disabled;
            }
        }).ToImmutableArray();
        else foreach (var key in keys) if (key.Enabled) { var gesture = HotkeyDefaults.Parse(key); used.Add((gesture.Key, gesture.Modifiers)); }
        // Existing scene keys reserve their gestures before new actions are introduced.
        var scenes = legacy ? Scenes.Select(s => s with { Trigger = Reserve(s.Trigger) }).ToImmutableArray() : Scenes;
        if (!legacy) foreach (var scene in scenes)
            if (scene.Trigger.Enabled) { var gesture = HotkeyDefaults.Parse(scene.Trigger); used.Add((gesture.Key, gesture.Modifiers)); }
        if (screenSendUpgrade is not null) keys = keys.Add(Reserve(screenSendUpgrade));
        foreach (var key in HotkeyDefaults.All)
            if (!keys.Any(k => k.Action == key.Action)) keys = keys.Add(Reserve(key));
        // Upgrade only the untouched stock prompt; retain user prompts and generation limits.
        if (scenes.Any(scene => scene.Id == "algorithm" && scene.SystemPrompt == SceneDefaults.LegacyAlgorithmPrompt))
            scenes = scenes.Select(scene => scene.Id == "algorithm" && scene.SystemPrompt == SceneDefaults.LegacyAlgorithmPrompt
                ? scene with { SystemPrompt = SceneDefaults.AlgorithmPrompt } : scene).ToImmutableArray();
        if (SchemaVersion < 5 && !ExclusiveHotkeysEnabled && !ExclusiveReaderKeys)
        {
            // Preserve which old exclusive bindings actually ran; the former screen-send
            // exception becomes an ordinary enabled row after this one-time migration.
            Hotkey PreserveEffectiveState(Hotkey key) => key.Mode == HotkeyMode.Exclusive && key.Action != "captureScreenSend"
                ? key with { Enabled = false } : key;
            keys = keys.Select(PreserveEffectiveState).ToImmutableArray();
            scenes = scenes.Select(scene => scene with { Trigger = PreserveEffectiveState(scene.Trigger) }).ToImmutableArray();
        }
        var connections = Backends.ToList();
        if (scenes.Any(scene => scene.LegacySettings is not null))
            scenes = scenes.Select(scene => MigrateScene(scene, connections)).ToImmutableArray();
        // Upgrade only the exact previous stock settings; preserve deliberately customized limits.
        var analysis = CodingAnalysisVersion == 0 && CodingAnalysis == new CodingRunOptions(12, 300, 180000)
            ? new CodingRunOptions() : CodingAnalysis;
        return this with { SchemaVersion = 6, CodingAnalysis = analysis, CodingAnalysisVersion = 2,
            Backends = connections.Count == Backends.Length ? Backends : connections.ToImmutableArray(), Hotkeys = keys, Scenes = scenes,
            ExclusiveHotkeysEnabled = false, ExclusiveReaderKeys = false };
    }
    private ScenePreset MigrateScene(ScenePreset scene, List<Backend> connections)
    {
        if (scene.LegacySettings is not { } old) return scene;
        string? Text(string key) => old.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
        var model = Text("Model");
        var temperature = old.TryGetValue("Temperature", out var temp) && temp.ValueKind != JsonValueKind.Null ? temp.GetDouble() : (double?)null;
        var tokens = old.TryGetValue("MaxTokens", out var limit) && limit.ValueKind != JsonValueKind.Null ? limit.GetInt32() : (int?)null;
        var result = scene with { LegacySettings = null };
        if (!string.IsNullOrWhiteSpace(model) || temperature is not null || tokens is not null)
        {
            var original = connections.FirstOrDefault(b => b.Id == (scene.BackendId ?? SelectedBackendId))
                ?? throw new SafeFailure("scene_backend_missing");
            var configured = original with { Model = string.IsNullOrWhiteSpace(model) ? original.Model : model,
                Temperature = temperature ?? original.Temperature, MaxTokens = tokens ?? original.MaxTokens };
            Policy.ValidateBackend(configured);
            if (configured != original)
            {
                var id = "scene-" + scene.Id;
                for (var suffix = 1; connections.Any(b => b.Id == id); suffix++) id = "scene-" + scene.Id + "-" + suffix;
                connections.Add(configured with { Id = id, Name = (scene.Name.Length > 70 ? scene.Name[..70] : scene.Name) + " · 模型配置" });
                result = result with { BackendId = id };
            }
        }
        // Custom template instructions move into the system prompt; input is always appended directly.
        var template = Text("UserTemplate");
        if (!string.IsNullOrWhiteSpace(template) && template != "{input}" &&
            template != "请协助处理以下开发任务：\n\n{input}" && template != "请分析并解答以下算法题：\n\n{input}")
        {
            var instruction = template.Replace("{input}", "", StringComparison.Ordinal).Trim();
            if (instruction.Length > 0) result = result with { SystemPrompt = result.SystemPrompt + "\n\n" + instruction };
        }
        return result;
    }

    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Superb", "settings.json");
    public static Configuration Load() => Load(FilePath);
    internal static Configuration Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1_048_576) throw new SafeFailure("config_too_large");
        var content = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<Configuration>(content, Json) ?? throw new SafeFailure("config_invalid");
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty(nameof(CodingAnalysisVersion), out _)) config = config with { CodingAnalysisVersion = 0 };
        var upgrade = config.CodingAnalysisVersion < 2 || config.SchemaVersion < 6 || (!config.Scenes.IsDefault && config.Scenes.Any(s => s?.LegacySettings is { Count: > 0 })) || (!config.Hotkeys.IsDefault && config.Hotkeys.Any(k => k?.Action == "ask"));
        config = config.Migrate();
        config.Validate();
        if (upgrade)
        {
            // Preserve the exact old settings before removed fields are dropped on disk.
            File.Copy(path, path + ".upgrade-" + Guid.NewGuid().ToString("N") + ".bak", false);
            config.Save(path);
        }
        return config;
    }
    public void Validate()
    {
        if (CodingAnalysis is null) throw new SafeFailure("coding_budget_invalid");
        CodingAnalysis.Validate();
        if (CodingAnalysisVersion is < 0 or > 2) throw new SafeFailure("config_invalid");
        if (SchemaVersion != 6 || Scenes.IsDefault || Scenes.Length > 24 || Backends.IsDefault || Hotkeys.IsDefault ||
            // Leave room for up to 24 migrated scene connections alongside the previous 32.
            Panel is null || Backends.Length > 64 || Hotkeys.Length > 32) throw new SafeFailure("config_invalid");
        foreach (var b in Backends) Policy.ValidateBackend(b);
        if (Hotkeys.Any(k => k is null || !HotkeyDefaults.All.Any(d => d.Action == k.Action))) throw new SafeFailure("hotkey_invalid");
        if (HotkeyDefaults.All.Any(d => !Hotkeys.Any(k => k.Action == d.Action))) throw new SafeFailure("hotkey_required_action_missing");
        if (Scenes.Any(s => s is null) || Scenes.Select(s => s.Id).Distinct().Count() != Scenes.Length) throw new SafeFailure("scene_invalid");
        foreach (var scene in Scenes)
        {
            if (string.IsNullOrWhiteSpace(scene.Id) || scene.Id.Length > 64 || scene.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                string.IsNullOrWhiteSpace(scene.Name) || scene.Name.Length > 80 || scene.SystemPrompt is null || scene.SystemPrompt.Length > 16000 ||
                scene.Trigger is null || scene.Trigger.Action != scene.Action)
                throw new SafeFailure("scene_invalid");
            if (scene.BackendId is not null && !Backends.Any(b => b.Id == scene.BackendId)) throw new SafeFailure("scene_backend_missing");
        }
        HotkeyDefaults.Validate(AllHotkeys());
        if (Backends.Select(x => x.Id).Distinct().Count() != Backends.Length) throw new SafeFailure("backend_id_duplicate");
        if (!string.IsNullOrEmpty(SelectedBackendId) && !Backends.Any(x => x.Id == SelectedBackendId))
            throw new SafeFailure("backend_missing");
        if (!double.IsFinite(Panel.Left) || !double.IsFinite(Panel.Top) || !double.IsFinite(Panel.Width) || !double.IsFinite(Panel.Height) ||
            Panel.Width is < 360 or > 1800 || Panel.Height is < 240 or > 1600 || Panel.FontSize is < 11 or > 36 || !double.IsFinite(Panel.FontSize) ||
            !double.IsFinite(Panel.BackgroundOpacity) || Panel.BackgroundOpacity is < 0.25 or > 1 ||
            !Theme.TryResolve(Panel.Theme, Panel.Accent, Panel.CustomBase, Panel.CustomInk, out _, out _))
            throw new SafeFailure("panel_layout_invalid");
    }
    public void Save() => Save(FilePath);
    internal void Save(string path)
    {
        Validate();
        var json = JsonSerializer.Serialize(this, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new SafeFailure("config_too_large");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".new";
        File.WriteAllText(temp, json);
        File.Move(temp, path, true);
    }
}
