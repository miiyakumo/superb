using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

internal sealed record PanelLayout(double Left = 80, double Top = 80, double Width = 540, double Height = 620, double FontSize = 16, bool Visible = false);
internal sealed record Configuration
{
    public int SchemaVersion { get; init; } = 2;
    public ImmutableArray<Backend> Backends { get; init; } = [];
    public string SelectedBackendId { get; init; } = "";
    public Routing Routing { get; init; } = new();
    public Pipeline Pipeline { get; init; } = Pipeline.Direct;
    public CaptureTarget? Capture { get; init; }
    public bool AutoCopy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExclusiveReaderKeys { get; init; }
    public bool CombineCaptureAndSend { get; init; }
    public bool ClearOwnedClipboardOnPause { get; init; } = true;
    public int SyncMinutes { get; init; } = 10;
    public PanelLayout Panel { get; init; } = new();
    public bool ExclusiveHotkeysEnabled { get; init; }
    public ImmutableArray<ScenePreset> Scenes { get; init; } = SceneDefaults.All;
    public ImmutableArray<Hotkey> Hotkeys { get; init; } = HotkeyDefaults.All;
    public IEnumerable<Hotkey> AllHotkeys() => Hotkeys.Concat(Scenes.Select(s => s.Trigger));
    public Configuration Migrate()
    {
        if (SchemaVersion is not (1 or 2)) throw new SafeFailure("config_invalid");
        if (Hotkeys.IsDefault || Scenes.IsDefault) throw new SafeFailure("config_invalid");
        var legacy = SchemaVersion == 1;
        var keys = Hotkeys;
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
        foreach (var key in HotkeyDefaults.All)
            if (!keys.Any(k => k.Action == key.Action)) keys = keys.Add(Reserve(key));
        var scenes = legacy ? Scenes.Select(s => s with { Trigger = Reserve(s.Trigger) }).ToImmutableArray() : Scenes;
        return this with { SchemaVersion = 2, Hotkeys = keys, Scenes = scenes, ExclusiveHotkeysEnabled = ExclusiveHotkeysEnabled || ExclusiveReaderKeys, ExclusiveReaderKeys = false };
    }
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Superb", "settings.json");
    public static Configuration Load()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 1_048_576) throw new SafeFailure("config_too_large");
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(FilePath), Json) ?? throw new SafeFailure("config_invalid");
        config = config.Migrate();
        config.Validate();
        // HWNDs are session scoped and must never be reused across process launches.
        return config.Capture?.Kind == CaptureKind.Window ? config with { Capture = null } : config;
    }
    public void Validate()
    {
        if (SchemaVersion != 2 || Scenes.IsDefault || Scenes.Length > 24 || Backends.IsDefault || Hotkeys.IsDefault || Pipeline is null || Pipeline.Steps.IsDefault ||
            Routing is null || Panel is null || SyncMinutes is < 1 or > 60 || Backends.Length > 32 || Hotkeys.Length > 32 ||
            Pipeline.Steps.Length > 32 || string.IsNullOrWhiteSpace(Pipeline.Version)) throw new SafeFailure("config_invalid");
        foreach (var b in Backends) Policy.ValidateBackend(b);
        if (Hotkeys.Any(k => k is null || !HotkeyDefaults.All.Any(d => d.Action == k.Action))) throw new SafeFailure("hotkey_invalid");
        if (HotkeyDefaults.All.Any(d => !Hotkeys.Any(k => k.Action == d.Action))) throw new SafeFailure("hotkey_required_action_missing");
        if (Scenes.Any(s => s is null) || Scenes.Select(s => s.Id).Distinct().Count() != Scenes.Length) throw new SafeFailure("scene_invalid");
        foreach (var scene in Scenes)
        {
            if (string.IsNullOrWhiteSpace(scene.Id) || scene.Id.Length > 64 || scene.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                string.IsNullOrWhiteSpace(scene.Name) || scene.Name.Length > 80 || scene.SystemPrompt is null || scene.SystemPrompt.Length > 16000 ||
                scene.UserTemplate is null || scene.UserTemplate.Length > 16000 || !scene.UserTemplate.Contains("{input}", StringComparison.Ordinal) ||
                !Enum.IsDefined(scene.Input) || scene.Trigger is null || scene.Trigger.Action != scene.Action || scene.Model is null || scene.Model.Length > 256 ||
                (scene.Temperature is { } temp && (!double.IsFinite(temp) || temp is < 0 or > 2)) || (scene.MaxTokens is { } tokens && tokens is < 1 or > 32768))
                throw new SafeFailure("scene_invalid");
            if (scene.BackendId is not null && !Backends.Any(b => b.Id == scene.BackendId)) throw new SafeFailure("scene_backend_missing");
        }
        HotkeyDefaults.Validate(AllHotkeys());
        if (Backends.Select(x => x.Id).Distinct().Count() != Backends.Length) throw new SafeFailure("backend_id_duplicate");
        foreach (var id in new[] { SelectedBackendId, Routing.TextBackendId, Routing.ImageBackendId }.Where(x => !string.IsNullOrEmpty(x)))
            if (!Backends.Any(x => x.Id == id)) throw new SafeFailure("route_backend_missing");
        if (!double.IsFinite(Panel.Left) || !double.IsFinite(Panel.Top) || !double.IsFinite(Panel.Width) || !double.IsFinite(Panel.Height) ||
            Panel.Width is < 360 or > 1800 || Panel.Height is < 240 or > 1600 || Panel.FontSize is < 11 or > 36 || !double.IsFinite(Panel.FontSize))
            throw new SafeFailure("panel_layout_invalid");
    }
    public void Save()
    {
        Validate();
        var json = JsonSerializer.Serialize(this, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new SafeFailure("config_too_large");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".new";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, true);
    }
}
