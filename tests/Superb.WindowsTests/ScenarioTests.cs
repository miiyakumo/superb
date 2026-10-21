using System.Collections.Immutable;
using System.Text.Json;
using Superb.Core;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class ScenarioTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Scenario assertion failed"); }
    private static void Reject(Action action, string code)
    {
        try { action(); throw new Exception("Invalid configuration accepted"); }
        catch (SafeFailure error) { Check(error.Code == code); }
    }
    internal static void Add(List<(string, Action)> tests)
    {
        tests.Add(("Schema 1 migration preserves custom keys and converts legacy exclusive reader settings", () =>
        {
            var old = new Configuration { SchemaVersion = 1, ExclusiveReaderKeys = true,
                Hotkeys = HotkeyDefaults.All.Where(k => k.Action is not ("ask" or "resume" or "retry")).Select(k => k.Action == "copy" ? k with { Gesture = "Ctrl+Alt+V" } : k).ToImmutableArray() };
            var migrated = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(old, Configuration.Json), Configuration.Json)!.Migrate();
            migrated.Validate();
            Check(migrated.SchemaVersion == 2 && !migrated.ExclusiveReaderKeys && migrated.ExclusiveHotkeysEnabled);
            Check(migrated.Hotkeys.Single(k => k.Action == "toggle") is { Gesture: "F6", Mode: HotkeyMode.Exclusive });
            Check(migrated.Hotkeys.Single(k => k.Action == "copy").Gesture == "Ctrl+Alt+V");
            Check(migrated.Hotkeys.Count(k => k.Action == "ask") == 1 && migrated.Scenes.Length == 3);
            Check(migrated.Migrate() == migrated);
        }));
        tests.Add(("Migration retains old bindings when new default actions or scenes would collide", () =>
        {
            var old = new Configuration { SchemaVersion = 1, Hotkeys = HotkeyDefaults.All.Where(k => k.Action is not ("ask" or "retry" or "resume"))
                .Select(k => k.Action == "copy" ? k with { Gesture = "Ctrl+Alt+C" } : k.Action == "send" ? k with { Gesture = "Ctrl+Alt+Enter" } : k).ToImmutableArray() };
            var next = old.Migrate(); next.Validate();
            Check(next.Hotkeys.Single(k => k.Action == "copy").Enabled && !next.Scenes.Single(s => s.Id == "coding").Trigger.Enabled);
            Check(next.Hotkeys.Single(k => k.Action == "send").Enabled && !next.Hotkeys.Single(k => k.Action == "ask").Enabled);
            var oldSingle = old with { Hotkeys = old.Hotkeys.Select(k => k.Action == "copy" ? k with { Gesture = "F9" } : k).ToImmutableArray() };
            var migratedSingle = oldSingle.Migrate(); migratedSingle.Validate();
            Check(migratedSingle.Hotkeys.Single(k => k.Action == "copy") is { Enabled: false, Mode: HotkeyMode.Exclusive, Gesture: "F9" });
        }));
        tests.Add(("Custom exclusive keys and disabled scene bindings round-trip without changing prompts", () =>
        {
            var config = new Configuration { ExclusiveHotkeysEnabled = true };
            config = config with { Hotkeys = config.Hotkeys.Select(k => k.Action == "toggle" ? k with { Mode = HotkeyMode.Exclusive, Gesture = "F2" } : k).ToImmutableArray(),
                Scenes = config.Scenes.Select(s => s.Id == "coding" ? s with { SystemPrompt = "Customized system", Trigger = s.Trigger with { Mode = HotkeyMode.Exclusive, Gesture = "F4", Enabled = false } } : s).ToImmutableArray() };
            config.Validate();
            var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config, Configuration.Json), Configuration.Json)!.Migrate();
            restored.Validate(); Check(restored.Scenes[0] == config.Scenes[0] && restored.Hotkeys.SequenceEqual(config.Hotkeys));
        }));
        tests.Add(("Duplicate keys across scenes and actions and reserved keys are rejected", () =>
        {
            var config = new Configuration();
            Reject(() => (config with { Scenes = config.Scenes.SetItem(0, config.Scenes[0] with { Trigger = config.Scenes[0].Trigger with { Gesture = "Ctrl+Alt+1" } }) }).Validate(), "hotkey_duplicate");
            Reject(() => HotkeyDefaults.Parse(new("test", "F12", HotkeyMode.Exclusive)), "hotkey_reserved");
            Reject(() => HotkeyDefaults.Parse(new("test", "Ctrl+F6", HotkeyMode.Exclusive)), "hotkey_exclusive_function_required");
            Reject(() => HotkeyDefaults.Parse(new("test", "F6")), "hotkey_combination_required");
            Reject(() => HotkeyDefaults.Parse(new("test", "Ctrl+Alt+Delete")), "hotkey_combination_required");
            Check(HotkeyDefaults.Parse(new("test", "F24", HotkeyMode.Exclusive)).Key == System.Windows.Input.Key.F24);
        }));
        tests.Add(("Scene validation rejects missing input placeholder and missing backend without rerouting", () =>
        {
            var config = new Configuration(); var scene = config.Scenes[0];
            Reject(() => (config with { Scenes = [scene with { UserTemplate = "fixed" }] }).Validate(), "scene_invalid");
            Reject(() => (config with { Scenes = [scene with { BackendId = "gone" }] }).Validate(), "scene_backend_missing");
            Reject(() => (config with { Scenes = [scene with { Temperature = double.NaN }] }).Validate(), "scene_invalid");
        }));
        tests.Add(("Scene preparation retains mandatory masks and checks image capability and total prompt size", () =>
        {
            var b = new Backend("vision", "Vision", "https://example.test/v1/chat/completions", "v", "", SupportsImages: true);
            var settings = new Configuration { Backends = [b], SelectedBackendId = b.Id, Pipeline = new("mask", [new(StepKind.Mask, Rectangle: new(0, 0, 1, 1))]) };
            var png = ImageProcessor.Encode(System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8));
            var snapshot = new InputSnapshot(Guid.NewGuid(), InputKind.Screen, png, 0, DateTimeOffset.UtcNow);
            var request = SceneRequestFactory.Prepare(settings, settings.Scenes[0], snapshot, new ImageProcessor());
            var pixels = new byte[16]; ImageProcessor.Decode(request.Content.Png).CopyPixels(pixels, 8, 0);
            Check(pixels[0] == 0 && request.Content.HasImage && request.SystemPrompt == settings.Scenes[0].SystemPrompt);
            Reject(() => SceneRequestFactory.Prepare(settings with { Backends = [b with { SupportsImages = false }] }, settings.Scenes[0], snapshot, new ImageProcessor()), "backend_has_no_vision");
            Reject(() => Policy.Check(request with { Backend = b with { MaxInputBytes = 1 } }), "input_too_large");
        }));
        tests.Add(("Independent scene prompts, model and backend selection reach the production HTTP adapter", () => TestHttp().GetAwaiter().GetResult()));
    }
    private static async Task TestHttp()
    {
        using var first = new SyntheticEndpoint(); using var second = new SyntheticEndpoint(); using var client = new ChatCompletionsClient(first);
        var a = new Backend("a", "First", first.Url, "base-a", "", AllowLoopbackHttp: true, Stream: false);
        var b = a with { Id = "b", Endpoint = second.Url, Model = "base-b" };
        var config = new Configuration { Backends = [a, b], SelectedBackendId = "a", Routing = new("a"), Pipeline = new("normalize", [new(StepKind.NormalizeText)]) };
        var coding = config.Scenes[0] with { BackendId = "b", Model = "coding-model", SystemPrompt = "Coding system", UserTemplate = "code:{input}", Temperature = 0.7, MaxTokens = 123 };
        var algorithm = config.Scenes[1] with { SystemPrompt = "Algorithm system", UserTemplate = "solve:{input}" };
        PreparedRequest Prepare(ScenePreset scene, string input) => SceneRequestFactory.Prepare(config, scene,
            new(Guid.NewGuid(), InputKind.ManualText, Payload.FromText(input), 0, DateTimeOffset.UtcNow), new ImageProcessor());
        var frozen = Prepare(coding, "  only coding  ");
        coding = coding with { SystemPrompt = "Must not affect in-flight request", Model = "changed" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in client.SendAsync(frozen, timeout.Token)) { }
        await foreach (var item in client.SendAsync(Prepare(algorithm, "  only algorithm  "), timeout.Token)) { }
        Check(first.Requests.Count == 1 && second.Requests.Count == 1);
        var code = second.Requests.Single(); var algo = first.Requests.Single();
        Check(code.GetProperty("model").GetString() == "coding-model" && code.GetProperty("temperature").GetDouble() == 0.7 && code.GetProperty("max_tokens").GetInt32() == 123);
        Check(code.GetProperty("messages").GetArrayLength() == 2 && code.GetProperty("messages")[0].GetProperty("role").GetString() == "system");
        Check(code.GetProperty("messages")[0].GetProperty("content").GetString() == "Coding system" && code.GetProperty("messages")[1].GetProperty("content").GetString() == "code:only coding");
        Check(algo.GetProperty("messages").GetArrayLength() == 2 && algo.GetProperty("messages")[0].GetProperty("content").GetString() == "Algorithm system");
        Check(algo.GetProperty("messages")[1].GetProperty("content").GetString() == "solve:only algorithm" && !algo.ToString().Contains("Coding system"));
    }
}
