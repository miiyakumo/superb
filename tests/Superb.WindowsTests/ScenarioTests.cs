using System.Collections.Immutable;
using System.IO;
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
        tests.Add(("Old send aliases merge deterministically and preserve customized or disabled bindings", () =>
        {
            var baseline = new Configuration { SchemaVersion = 2 };
            var oldSend = new Hotkey("send", "Ctrl+Alt+7");
            var oldAsk = new Hotkey("ask", "Ctrl+Alt+Enter");
            void CheckBinding(Hotkey? send, Hotkey? ask, Hotkey expected)
            {
                var old = baseline with { Hotkeys = baseline.Hotkeys.Where(k => k.Action != "send")
                    .Concat(new[] { send, ask }.OfType<Hotkey>()).ToImmutableArray() };
                var migrated = old.Migrate(); migrated.Validate();
                Check(migrated.Hotkeys.Single(k => k.Action == "send") == expected);
                Check(!migrated.Hotkeys.Any(k => k.Action == "ask") && migrated.Migrate() == migrated);
            }
            CheckBinding(oldSend, oldAsk, oldAsk with { Action = "send" });
            var customSend = oldSend with { Gesture = "F23", Mode = HotkeyMode.Exclusive };
            CheckBinding(customSend, oldAsk, customSend with { Enabled = false });
            CheckBinding(oldSend with { Enabled = false }, oldAsk, oldSend with { Enabled = false });
            CheckBinding(oldSend, oldAsk with { Enabled = false }, oldSend);
            CheckBinding(null, oldAsk with { Gesture = "Ctrl+Shift+Enter", Enabled = false },
                oldAsk with { Action = "send", Gesture = "Ctrl+Shift+Enter", Enabled = false });
            CheckBinding(oldSend, null, oldSend);
            CheckBinding(null, null, HotkeyDefaults.All.Single(k => k.Action == "send"));
        }));
        tests.Add(("Schema upgrade backs up exact old JSON, drops removed fields and retains saved product preferences", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "superb-upgrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "settings.json");
                var b = new Backend("saved", "Saved", "https://example.test/chat/completions", "custom-model", "synthetic-reference");
                var old = new Configuration { SchemaVersion = 2, Backends = [b], SelectedBackendId = b.Id,
                    AutoCopy = true, Panel = new(FontSize: 24),
                    Scenes = [SceneDefaults.All[0] with { SystemPrompt = "custom prompt", BackendId = b.Id }] };
                var json = JsonSerializer.Serialize(old, Configuration.Json);
                json = json.Insert(json.LastIndexOf('}'), ",\"Pipeline\":{\"Steps\":[{\"Kind\":\"Mask\"}]},\"Capture\":{\"Kind\":\"Window\"},\"SyncMinutes\":15,\"CombineCaptureAndSend\":true,\"Routing\":{\"TextBackendId\":\"gone\",\"ImageBackendId\":\"gone\"}");
                File.WriteAllText(path, json);
                var upgraded = Configuration.Load(path); upgraded.Validate();
                Check(upgraded.SchemaVersion == 6 && upgraded.AutoCopy && upgraded.Panel == old.Panel);
                Check(upgraded.Backends.SequenceEqual(old.Backends) && upgraded.Scenes.SequenceEqual(old.Scenes));
                var backups = Directory.GetFiles(directory, "*.bak");
                Check(backups.Length == 1 && File.ReadAllText(backups[0]) == json);
                using var saved = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var removed in new[] { "Pipeline", "Capture", "SyncMinutes", "CombineCaptureAndSend", "Routing" })
                    Check(!saved.RootElement.TryGetProperty(removed, out _));
                Configuration.Load(path);
                Check(Directory.GetFiles(directory, "*.bak").Length == 1);
                const string invalid = "{\"SchemaVersion\":99}";
                File.WriteAllText(path, invalid);
                Reject(() => Configuration.Load(path), "config_invalid");
                Check(File.ReadAllText(path) == invalid && Directory.GetFiles(directory, "*.bak").Length == 1);
            }
            finally { Directory.Delete(directory, true); }
        }));
        tests.Add(("Schema 1 migration preserves custom keys and converts legacy exclusive reader settings", () =>
        {
            var old = new Configuration { SchemaVersion = 1, ExclusiveReaderKeys = true,
                Hotkeys = HotkeyDefaults.All.Where(k => k.Action is not ("ask" or "resume" or "retry")).Select(k => k.Action == "copy" ? k with { Gesture = "Ctrl+Alt+V" } : k).ToImmutableArray() };
            var migrated = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(old, Configuration.Json), Configuration.Json)!.Migrate();
            migrated.Validate();
            Check(migrated.SchemaVersion == 6 && !migrated.ExclusiveReaderKeys && !migrated.ExclusiveHotkeysEnabled);
            Check(migrated.Hotkeys.Single(k => k.Action == "toggle") is { Gesture: "F6", Mode: HotkeyMode.Exclusive });
            Check(migrated.Hotkeys.Single(k => k.Action == "copy").Gesture == "Ctrl+Alt+V");
            Check(!migrated.Hotkeys.Any(k => k.Action == "ask") && migrated.Scenes.Length == 3);
            Check(migrated.Migrate() == migrated);
        }));
        tests.Add(("Migration retains old bindings when new default actions or scenes would collide", () =>
        {
            var old = new Configuration { SchemaVersion = 1, Hotkeys = HotkeyDefaults.All.Where(k => k.Action is not ("ask" or "retry" or "resume"))
                .Select(k => k.Action == "copy" ? k with { Gesture = "Ctrl+Alt+C" } : k.Action == "send" ? k with { Gesture = "Ctrl+Alt+Enter" } : k).ToImmutableArray() };
            var next = old.Migrate(); next.Validate();
            Check(next.Hotkeys.Single(k => k.Action == "copy").Enabled && !next.Scenes.Single(s => s.Id == "coding").Trigger.Enabled);
            Check(next.Hotkeys.Single(k => k.Action == "send").Enabled && !next.Hotkeys.Any(k => k.Action == "ask"));
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
        tests.Add(("Only the stock algorithm prompt is upgraded; custom instructions and generation settings survive", () =>
        {
            var scene = SceneDefaults.All[1] with { SystemPrompt = SceneDefaults.LegacyAlgorithmPrompt,
                Trigger = new("scene:algorithm", "Ctrl+Alt+Z") };
            var config = new Configuration { Scenes = [scene] };
            var migrated = config.Migrate();
            Check(migrated.Scenes.Single() == scene with { SystemPrompt = SceneDefaults.AlgorithmPrompt });
            Check(migrated.Migrate() == migrated);
            var custom = scene with { SystemPrompt = "User supplied algorithm instructions" };
            Check((config with { Scenes = [custom] }).Migrate().Scenes.Single() == custom);
            var backend = new Backend("ds", "Synthetic", DeepSeekSetup.Endpoint, "deepseek-flash", "", ThinkingMode: ThinkingMode.Disabled);
            var saved = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(new Configuration { Backends = [backend], SelectedBackendId = backend.Id }, Configuration.Json), Configuration.Json)!;
            Check(saved.Backends.Single().ThinkingMode == ThinkingMode.Disabled);
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
        tests.Add(("Scene validation rejects missing backend without rerouting", () =>
        {
            var config = new Configuration(); var scene = config.Scenes[0];
            Reject(() => (config with { Scenes = [scene with { BackendId = "gone" }] }).Validate(), "scene_backend_missing");
        }));
        tests.Add(("Scene preparation preserves encoded images and checks image capability and total prompt size", () =>
        {
            var b = new Backend("vision", "Vision", "https://example.test/v1/chat/completions", "v", "", SupportsImages: true);
            var settings = new Configuration { Backends = [b], SelectedBackendId = b.Id };
            var png = ImageProcessor.Encode(System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8));
            var snapshot = new InputSnapshot(Guid.NewGuid(), InputKind.Screen, png, 0, DateTimeOffset.UtcNow);
            var request = SceneRequestFactory.Prepare(settings, settings.Scenes[0], snapshot);
            var pixels = new byte[16]; ImageProcessor.Decode(request.Content.Png).CopyPixels(pixels, 8, 0);
            Check(pixels[0] == 255 && request.Content.HasImage && request.SystemPrompt == settings.Scenes[0].SystemPrompt);
            Reject(() => SceneRequestFactory.Prepare(settings with { Backends = [b with { SupportsImages = false }] }, settings.Scenes[0], snapshot), "backend_has_no_vision");
            Reject(() => Policy.Check(request with { Backend = b with { MaxInputBytes = 1 } }), "input_too_large");
        }));
        tests.Add(("Legacy scene overrides migrate once into independent connections and custom template instructions become system prompts", () =>
        {
            var backend = new Backend("base", "Base", "https://example.test/chat/completions", "base-model", "shared-key",
                MaxInputBytes: 1234567, MaxOutputChars: 234567, Stream: false, SendTemperature: false,
                TokenLimitParameter: TokenLimitParameter.MaxCompletionTokens, ProxyMode: ProxyMode.System);
            var scene = JsonSerializer.Deserialize<ScenePreset>("""
                {"Id":"custom","Name":"Custom","SystemPrompt":"System instructions","UserTemplate":"Solve carefully: {input}",
                 "Input":"Screen","Model":"custom-model","Temperature":0.7,"MaxTokens":1234,
                 "Trigger":{"Action":"scene:custom","Gesture":"Ctrl+Alt+Z","Enabled":false}}
                """, Configuration.Json)!;
            var old = new Configuration { SchemaVersion = 2, Backends = [backend], SelectedBackendId = backend.Id, Scenes = [scene] };
            var migrated = old.Migrate(); migrated.Validate();
            var next = migrated.Scenes.Single(); var connection = migrated.Backends.Single(b => b.Id == next.BackendId);
            Check(connection == backend with { Id = connection.Id, Name = "Custom · 模型配置", Model = "custom-model", Temperature = 0.7, MaxTokens = 1234 });
            Check(migrated.Backends[0] == backend && migrated.SelectedBackendId == backend.Id);
            Check(next.SystemPrompt == "System instructions\n\nSolve carefully:" && next.Trigger == scene.Trigger && next.LegacySettings is null);
            Check(migrated.Migrate() == migrated);
            var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(migrated, Configuration.Json), Configuration.Json)!.Migrate();
            restored.Validate(); Check(restored.Backends.SequenceEqual(migrated.Backends) && restored.Scenes.Single() == next);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(migrated, Configuration.Json));
            var savedScene = json.RootElement.GetProperty("Scenes")[0];
            foreach (var removed in new[] { "Model", "Temperature", "MaxTokens", "UserTemplate", "Input", "LegacySettings" })
                Check(!savedScene.TryGetProperty(removed, out _));
        }));
        tests.Add(("Scene connections inherit the current default and never select by input type or fall back from a missing pin", () =>
        {
            var text = new Backend("text", "Text", "https://example.test/chat/completions", "text-model", "");
            var vision = text with { Id = "vision", Name = "Vision", Model = "vision-model", SupportsImages = true };
            var settings = new Configuration { Backends = [text, vision], SelectedBackendId = text.Id };
            var scene = settings.Scenes[0];
            var snapshot = new InputSnapshot(Guid.NewGuid(), InputKind.ManualText, Payload.FromText("  verbatim {input}\n"), 0, DateTimeOffset.UtcNow);
            var request = SceneRequestFactory.Prepare(settings, scene, snapshot);
            Check(request.Backend == text && request.Content == snapshot.Content && request.Snapshot == snapshot);
            Check(SceneRequestFactory.Prepare(settings with { SelectedBackendId = vision.Id }, scene, snapshot).Backend == vision);
            Check(SceneRequestFactory.Prepare(settings, scene with { BackendId = vision.Id }, snapshot).Backend == vision);
            var image = snapshot with { Content = new Payload(null, [1], 1, 1) };
            Reject(() => SceneRequestFactory.Prepare(settings, scene, image), "backend_has_no_vision");
            Check(SceneRequestFactory.Prepare(settings, scene with { BackendId = vision.Id }, image).Backend == vision);
            Reject(() => SceneRequestFactory.Prepare(settings, scene with { BackendId = "gone" }, snapshot), "scene_backend_missing");
            Check(SceneRequestFactory.Prepare(settings, null, snapshot).Backend == text);
        }));
        tests.Add(("Independent scene prompts, model and backend selection reach the production HTTP adapter", () => TestHttp().GetAwaiter().GetResult()));
    }
    private static async Task TestHttp()
    {
        using var first = new SyntheticEndpoint(); using var second = new SyntheticEndpoint(); using var client = new ChatCompletionsClient(first);
        var a = new Backend("a", "First", first.Url, "base-a", "", AllowLoopbackHttp: true, Stream: false);
        var b = a with { Id = "b", Endpoint = second.Url, Model = "coding-model", Temperature = 0.7, MaxTokens = 123 };
        var config = new Configuration { Backends = [a, b], SelectedBackendId = "a" };
        var coding = config.Scenes[0] with { BackendId = "b", SystemPrompt = "Coding system" };
        var algorithm = config.Scenes[1] with { SystemPrompt = "Algorithm system" };
        PreparedRequest Prepare(ScenePreset scene, string input) => SceneRequestFactory.Prepare(config, scene,
            new(Guid.NewGuid(), InputKind.ManualText, Payload.FromText(input), 0, DateTimeOffset.UtcNow));
        var frozen = Prepare(coding, "  only coding  ");
        coding = coding with { SystemPrompt = "Must not affect in-flight request" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var item in client.SendAsync(frozen, timeout.Token)) { }
        await foreach (var item in client.SendAsync(Prepare(algorithm, "  only algorithm  "), timeout.Token)) { }
        Check(first.Requests.Count == 1 && second.Requests.Count == 1);
        var code = second.Requests.Single(); var algo = first.Requests.Single();
        Check(code.GetProperty("model").GetString() == "coding-model" && code.GetProperty("temperature").GetDouble() == 0.7 && code.GetProperty("max_tokens").GetInt32() == 123);
        Check(code.GetProperty("messages").GetArrayLength() == 2 && code.GetProperty("messages")[0].GetProperty("role").GetString() == "system");
        Check(code.GetProperty("messages")[0].GetProperty("content").GetString() == "Coding system" && code.GetProperty("messages")[1].GetProperty("content").GetString() == "  only coding  ");
        Check(algo.GetProperty("messages").GetArrayLength() == 2 && algo.GetProperty("messages")[0].GetProperty("content").GetString() == "Algorithm system");
        Check(algo.GetProperty("messages")[1].GetProperty("content").GetString() == "  only algorithm  " && !algo.ToString().Contains("Coding system"));
    }
}
