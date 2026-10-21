using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

internal sealed record Hotkey(string Action, string Gesture);
internal sealed record PanelLayout(double Left = 80, double Top = 80, double Width = 540, double Height = 620, double FontSize = 16, bool Visible = false);
internal sealed record Configuration
{
    public int SchemaVersion { get; init; } = 1;
    public ImmutableArray<Backend> Backends { get; init; } = [];
    public string SelectedBackendId { get; init; } = "";
    public Routing Routing { get; init; } = new();
    public Pipeline Pipeline { get; init; } = Pipeline.Direct;
    public CaptureTarget? Capture { get; init; }
    public bool AutoCopy { get; init; }
    public bool ExclusiveReaderKeys { get; init; }
    public bool CombineCaptureAndSend { get; init; }
    public bool ClearOwnedClipboardOnPause { get; init; } = true;
    public int SyncMinutes { get; init; } = 10;
    public PanelLayout Panel { get; init; } = new();
    public ImmutableArray<Hotkey> Hotkeys { get; init; } =
    [new("clipboard", "Ctrl+Alt+1"), new("capture", "Ctrl+Alt+2"), new("toggle", "Ctrl+Alt+3"),
     new("copy", "Ctrl+Alt+4"), new("cancel", "Ctrl+Alt+5"), new("pause", "Ctrl+Alt+6"),
     new("send", "Ctrl+Alt+7"), new("pageDown", "Ctrl+Alt+8"), new("pageUp", "Ctrl+Alt+9"),
     new("settings", "Ctrl+Alt+0"), new("exit", "Ctrl+Alt+Q")];
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Superb", "settings.json");
    public static Configuration Load()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 1_048_576) throw new SafeFailure("config_too_large");
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(FilePath), Json) ?? throw new SafeFailure("config_invalid");
        config.Validate();
        // HWNDs are session scoped and must never be reused across process launches.
        return config.Capture?.Kind == CaptureKind.Window ? config with { Capture = null } : config;
    }
    public void Validate()
    {
        if (SchemaVersion != 1 || Backends.IsDefault || Hotkeys.IsDefault || Pipeline is null || Pipeline.Steps.IsDefault ||
            Routing is null || Panel is null || SyncMinutes is < 1 or > 60 || Backends.Length > 32 || Hotkeys.Length > 32 ||
            Pipeline.Steps.Length > 32 || string.IsNullOrWhiteSpace(Pipeline.Version)) throw new SafeFailure("config_invalid");
        foreach (var b in Backends) Policy.ValidateBackend(b);
        if (ExclusiveReaderKeys && ExclusiveReaderHotkeys.Keys.Keys.Any(action => Hotkeys.Count(k => k.Action == action) != 1))
            throw new SafeFailure("hotkey_required_action_missing");
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
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, true);
    }
}
