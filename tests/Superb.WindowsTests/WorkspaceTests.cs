using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using Superb.Core;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class WorkspaceTests
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    internal static void ChooseWindow(Window window)
    {
        // Test setup only: a background test runner may not have foreground permission.
        // Establish a real positive control before asserting that production code leaves it alone.
        var thread = GetCurrentThreadId();
        var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try
        {
            window.Activate();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
        }
        finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
        Pump();
        Check(Native.GetForegroundWindow() == new WindowInteropHelper(window).Handle);
    }
    private static void Check(bool value) { if (!value) throw new Exception("Workspace assertion failed"); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
        Check(predicate());
    }
    private static void Reject(string code, Action action)
    {
        try { action(); throw new Exception("Expected rejection: " + code); }
        catch (SafeFailure failure) { Check(failure.Code == code); }
    }
    internal static void Add(List<(string, Action)> tests, string[] args)
    {
        MarkdownTests.Add(tests, args);
        ScreenSendTests.Add(tests);
        PassThroughTests.Add(tests, args);
        if (args.Contains("--native-pointer")) PointerFocusTests.Add(tests);
        tests.Add(("Mouse activation policy rejects hover and non-input clicks even while editing", () =>
        {
            var window = new Window { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var protection = new Protection(window, true);
                Check(protection.Install());
                var hwnd = new WindowInteropHelper(window).Handle;
                var origin = Native.GetForegroundWindow();
                protection.AllowMouseActivation = () => true;
                Check(PointerFocusTests.MouseActivation(hwnd, 0x201) == 3);
                Check(protection.SetInteraction(true));
                Check(PointerFocusTests.MouseActivation(hwnd, 0x200) == 3); // hover
                Check(PointerFocusTests.MouseActivation(hwnd, 0x204) == 3); // right click
                protection.AllowMouseActivation = () => false;
                Check(PointerFocusTests.MouseActivation(hwnd, 0x201) == 3);
                protection.AllowMouseActivation = () => true;
                Check(PointerFocusTests.MouseActivation(hwnd, 0x201) == 1);
                Check(Native.GetForegroundWindow() == origin && protection.Check());
            }
            finally { window.Close(); }
        }));
        tests.Add(("Read-only reader rejects keyboard focus and keeps a normal arrow during passive corner resizing", () =>
        {
            var reader = new ReaderWindow();
            try
            {
                var hwnd = new WindowInteropHelper(reader).EnsureHandle(); reader.Show(); Pump();
                reader.SetActions(true, false, true, false, false, false);
                Check(reader.FindName("DraftText") is null && reader.FindName("InteractionButton") is null);
                var origin = Native.GetForegroundWindow();
                foreach (var message in new[] { 0x200, 0x201, 0x203, 0x204 }) Check(PointerFocusTests.MouseActivation(hwnd, message) == 3);
                var answer = (AnswerView)reader.FindName("Answer");
                Check(!answer.Reader.Focusable && !answer.Reader.IsSelectionEnabled && reader.ResizeMode == ResizeMode.NoResize);
                var resize = (Thumb)reader.FindName("ResizeHandle");
                Check(resize.Cursor == Cursors.Arrow && resize.ForceCursor && !resize.Focusable);
                var before = reader.Width;
                resize.RaiseEvent(new DragDeltaEventArgs(20, 10) { RoutedEvent = Thumb.DragDeltaEvent }); Pump();
                Check(reader.Width > before && resize.Cursor == Cursors.Arrow && Native.GetForegroundWindow() == origin && reader.Check());
                Check(reader.SetClickThrough(true) && reader.InputRegions().Count == 0);
                Check(reader.SetClickThrough(false) && reader.InputRegions().Count > 0 && Native.GetForegroundWindow() == origin);
            }
            finally { reader.Finish(); }
        }));
        tests.Add(("Settings reject hover activation while permitting deliberate mouse clicks", () =>
        {
            var window = new MainWindow(new Configuration(), new WorkspaceBackend());
            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var origin = Native.GetForegroundWindow();
                Check(PointerFocusTests.MouseActivation(hwnd, 0x200) == 3);
                Check(PointerFocusTests.MouseActivation(hwnd, 0x201) == 1);
                Check(PointerFocusTests.MouseActivation(hwnd, 0x204) == 1);
                Check(PointerFocusTests.MouseActivation(hwnd, 0xa1) == 1); // title bar click
                Check(PointerFocusTests.MouseActivation(hwnd, 0xa0) == 3); // title bar hover
                Check(Native.GetForegroundWindow() == origin);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Settings emergency buttons reject mouse activation and keyboard focus while configuration remains interactive", () =>
        {
            var window = new MainWindow(new Configuration(), new WorkspaceBackend());
            var host = new Window { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                foreach (var name in new[] { "SettingsPauseButton", "ResumeButton" })
                {
                    var button = (Button)window.FindName(name);
                    Check(!button.Focusable && !window.CanActivateSettingsTarget(button));
                    button.ApplyTemplate();
                    var caption = new TextBlock(); button.Content = caption;
                    Check(!window.CanActivateSettingsTarget(caption));
                }
                Check(window.CanActivateSettingsTarget((PasswordBox)window.FindName("ApiKey")));
                new WindowInteropHelper(host).EnsureHandle();
                var policy = new Protection(host, false); Check(policy.Install());
                var hwnd = new WindowInteropHelper(host).Handle;
                policy.AllowMouseActivation = () => false;
                Check(PointerFocusTests.MouseActivation(hwnd, 0x201) == 3 && PointerFocusTests.MouseActivation(hwnd, 0x203) == 3);
                policy.AllowMouseActivation = () => true;
                Check(PointerFocusTests.MouseActivation(hwnd, 0x201) == 1 && PointerFocusTests.MouseActivation(hwnd, 0x200) == 3);
            }
            finally { host.Close(); window.ExitApplication(); }
        }));
        tests.Add(("Settings pages save independently and reopening settings preserves the floating task and selected page", () =>
        {
            var backend = new Backend("test", "Synthetic", "https://example.test/v1/chat/completions", "model", "");
            var config = new Configuration { Backends = [backend], SelectedBackendId = backend.Id };
            var window = new MainWindow(config, new WorkspaceBackend());
            try
            {
                Check(((StackPanel)window.FindName("BackendSettings")).Visibility == Visibility.Visible);
                Check(!((Expander)window.FindName("BackendAdvancedSettings")).IsExpanded);
                var rows = (IEnumerable<HotkeyEditorRow>)((ItemsControl)window.FindName("HotkeyRows")).ItemsSource;
                rows.Single(r => r.Action == "clipboard").Gesture = "Ctrl+Shift+B";
                ((CheckBox)window.FindName("AutoCopy")).IsChecked = !config.AutoCopy;
                var keys = window.BuildHotkeySettings(); keys.Validate();
                Check(keys.Hotkeys.Single(k => k.Action == "clipboard").Gesture == "Ctrl+Shift+B");
                Check(keys.AutoCopy == config.AutoCopy && keys.Panel == config.Panel);
                var preferences = window.BuildClipboardPreferences(); preferences.Validate();
                Check(preferences.AutoCopy != config.AutoCopy);
                Check(preferences.Hotkeys == config.Hotkeys && preferences.Scenes == config.Scenes && preferences.Backends == config.Backends);
                new WindowInteropHelper(window).EnsureHandle();
                var reader = window.WorkspaceWindow; reader.Show(); Pump();
                window.QueueText("retained floating draft"); reader.Render("retained floating answer", "已完成");
                var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedItem = window.FindName("ScenesTab");
                ((Button)window.FindName("OpenHotkeysButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(tabs.SelectedItem == window.FindName("ShortcutsTab"));
                window.RunCommand("settings").GetAwaiter().GetResult(); Pump();
                Check(tabs.SelectedItem == window.FindName("ShortcutsTab") && window.WorkspaceCoordinator.Mode == RunState.Ready);
                Check(window.QueuedText() == "retained floating draft" && ((AnswerView)reader.FindName("Answer")).Text == "retained floating answer" && reader.Check());
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Model management exposes deletion, retains saved limits and defaults new models to 200k", () =>
        {
            var backend = new Backend("test", "Synthetic", "https://example.test/v1/chat/completions", "model", "", MaxTokens: 4096);
            var window = new MainWindow(new Configuration { Backends = [backend], SelectedBackendId = backend.Id }, new WorkspaceBackend());
            try
            {
                Check(((Button)window.FindName("DeleteModelButton")).IsEnabled);
                Check(((TextBox)window.FindName("MaxTokens")).Text == "4096");
                Check(((StackPanel)window.FindName("ClipboardPreferences")).Visibility == Visibility.Visible);
                ((Button)window.FindName("NewModelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((TextBox)window.FindName("MaxTokens")).Text == "200000");
                Check(window.FindName("MaxOutput") is null && window.FindName("InputLimit") is null);
                Check(!((Button)window.FindName("DeleteModelButton")).IsEnabled);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Connection editor preserves exact hidden limits and advanced compatibility while tokens timeout and thinking remain adjustable", () =>
        {
            var backend = new Backend("saved", "Saved", DeepSeekSetup.Endpoint, "model", "shared-key", SupportsImages: true,
                Stream: false, MaxInputBytes: 1234567, MaxOutputChars: 234567, TimeoutSeconds: 321, MaxTokens: 8192,
                SendTemperature: false, TokenLimitParameter: TokenLimitParameter.MaxCompletionTokens, ProxyMode: ProxyMode.System,
                ThinkingMode: ThinkingMode.Disabled);
            var window = new MainWindow(new Configuration { Backends = [backend], SelectedBackendId = backend.Id }, new WorkspaceBackend());
            try
            {
                Check(window.BuildBackendSettings() == backend);
                Check(window.FindName("InputLimit") is null && window.FindName("MaxOutput") is null && window.FindName("RoutingSettings") is null);
                foreach (var removed in new[] { "SceneModel", "SceneTemperature", "SceneTokens", "SceneUserTemplate", "SceneInputPicker" })
                    Check(window.FindName(removed) is null);
                var advanced = (Expander)window.FindName("BackendAdvancedSettings");
                Check(!advanced.IsExpanded);
                bool InsideAdvanced(DependencyObject control)
                {
                    for (var node = control; node is not null; node = LogicalTreeHelper.GetParent(node))
                        if (node == advanced) return true;
                    return false;
                }
                foreach (var name in new[] { "Vision", "Loopback", "Streaming", "ProxyModePicker", "SendTemperature", "UseCompletionTokens" })
                    Check(InsideAdvanced((DependencyObject)window.FindName(name)));
                foreach (var name in new[] { "MaxTokens", "TimeoutSeconds", "ThinkingModePicker" })
                    Check(!InsideAdvanced((DependencyObject)window.FindName(name)));
                ((TextBox)window.FindName("MaxTokens")).Text = "16384";
                ((TextBox)window.FindName("TimeoutSeconds")).Text = "600";
                ((ComboBox)window.FindName("ThinkingModePicker")).SelectedIndex = (int)ThinkingMode.Enabled;
                var edited = window.BuildBackendSettings();
                Check(edited == backend with { MaxTokens = 16384, TimeoutSeconds = 600, ThinkingMode = ThinkingMode.Enabled });
                var saved = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(new Configuration { Backends = [edited], SelectedBackendId = edited.Id }, Configuration.Json), Configuration.Json)!;
                saved.Validate(); Check(saved.Backends.Single() == edited);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Deleting models preserves unrelated defaults and protects scene references", () =>
        {
            var first = new Backend("first", "First", "https://example.test/v1/chat/completions", "model", "");
            var second = first with { Id = "second", Name = "Second", SupportsImages = true };
            var config = new Configuration { Backends = [first, second], SelectedBackendId = second.Id };
            var removed = config.WithoutBackend(first.Id); removed.Validate();
            Check(removed.Backends.SequenceEqual([second]) && removed.SelectedBackendId == second.Id);
            Check(removed.Scenes == config.Scenes);
            var selectedRemoved = config.WithoutBackend(second.Id); selectedRemoved.Validate();
            Check(selectedRemoved.SelectedBackendId == first.Id);
            var empty = removed.WithoutBackend(second.Id); empty.Validate();
            Check(empty.Backends.IsEmpty && empty.SelectedBackendId == "");
            Reject("scene_backend_in_use", () => (config with { Scenes = [config.Scenes[0] with { BackendId = first.Id }] }).WithoutBackend(first.Id));
            Reject("backend_missing", () => config.WithoutBackend("missing"));
            Check(config.Backends.Length == 2);
        }));
        tests.Add(("Scene shortcuts select a scene without sending; explicit send freezes the queued input", () =>
        {
            var backend = new Backend("test", "Synthetic", "https://example.test/v1/chat/completions", "model", "", SupportsImages: true);
            var scene = SceneDefaults.All.Single(s => s.Id == "algorithm");
            var client = new WorkspaceBackend();
            var window = new MainWindow(new Configuration { Backends = [backend], SelectedBackendId = backend.Id, Scenes = [scene] }, client);
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                new WindowInteropHelper(window).EnsureHandle(); var reader = window.WorkspaceWindow; reader.Show(); Pump();
                var picture = ImageProcessor.Encode(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8));
                window.QueueSnapshot(new(Guid.NewGuid(), InputKind.Screen, picture, 1, DateTimeOffset.UtcNow), "queued screenshot");
                window.QueueText("synthetic question from the floating input");
                window.RunCommand(scene.Action).GetAwaiter().GetResult();
                Check(client.Requests.Count == 0 && reader.SelectedSceneId == scene.Id);
                var pending = window.RunCommand("send"); Until(() => client.Requests.Count == 1 || pending.IsCompleted);
                var request = client.Requests.Single();
                Check(request.SceneId == scene.Id && request.Snapshot.Kind == InputKind.Screen && request.Content.Png == picture.Png && request.Content.Text!.Contains(window.QueuedText()));
                client.Events.Writer.TryWrite(new("synthetic result")); client.Events.Writer.TryWrite(new(Completed: true)); client.Events.Writer.Complete();
                Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.Latest is { State: RequestState.Completed, Text: "synthetic result" });
            }
            finally { window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
        }));
        tests.Add(("Shortcut hints distinguish registration, disabled keys and inactive exclusive capture", () =>
        {
            var custom = new Hotkey("clipboard", "Ctrl+Shift+B");
            Check(ShortcutLabel.Describe(custom, true).Text == "Ctrl+Shift+B");
            Check(ShortcutLabel.Describe(custom with { Enabled = false }, false).Text == "已停用");
            Check(ShortcutLabel.Describe(custom, false, "win32_1409").Text == "未生效");
            Check(!ShortcutLabel.Describe(custom, false, "win32_1409").Active);
            Check(ShortcutLabel.Describe(new("clipboard", "F1", HotkeyMode.Exclusive), false).Text == "未注册");
            Check(ShortcutLabel.Describe(new("clipboard", "F1", HotkeyMode.Exclusive), true).Text == "F1");
            Check(ShortcutLabel.Describe(null, false).Text == "未设置");
        }));
        tests.Add(("Floating key captions follow custom bindings and each button's exact current action", () =>
        {
            var reader = new ReaderWindow();
            try
            {
                new WindowInteropHelper(reader).EnsureHandle();
                var labels = HotkeyDefaults.All.ToDictionary(k => k.Action, k => ShortcutLabel.Describe(k, true));
                reader.SetActions(true, false, true, true, false, true); reader.SetShortcutHints(labels);
                string Caption(string name) => ShortcutHint.GetText((Button)reader.FindName(name));
                Check(Caption("ClipboardButton") == "Ctrl+Alt+1" && Caption("SettingsButton") == "Ctrl+Alt+0");
                labels = new(labels) { ["clipboard"] = ShortcutLabel.Describe(new("clipboard", "Ctrl+Shift+B"), true) };
                reader.SetShortcutHints(labels); Check(Caption("ClipboardButton") == "Ctrl+Shift+B");
                reader.SetActions(true, false, true, true, true, true); Check(Caption("CopyButton") == "仅鼠标");
                reader.SetActions(false, false, true, true, false, true); Check(Caption("PauseButton") == "Ctrl+Alt+S");
                reader.SetSelecting(true); Check(Caption("StopButton") == "Esc / 右键");
                reader.SetSelecting(false); Check(Caption("StopButton") == "Ctrl+Alt+5");
                var draft = new WorkspaceDraft();
                for (var i = 0; i < 2; i++) draft.Add(new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText("synthetic"), 1, DateTimeOffset.UtcNow), "text");
                reader.SetInputs(draft.Items);
                var tiles = ((ItemsControl)reader.FindName("InputQueue")).Items.Cast<object>().ToArray();
                string Remove(object tile) => (string)tile.GetType().GetProperty("RemoveHint")!.GetValue(tile)!;
                Check(Remove(tiles[0]) == "仅鼠标" && Remove(tiles[1]) == "Ctrl+Alt+Backspace");
                labels = new(labels) { ["deleteCapture"] = ShortcutLabel.Describe(new("deleteCapture", "Ctrl+Alt+Backspace", Enabled: false), false) };
                reader.SetShortcutHints(labels); Check(Remove(tiles[1]) == "已停用");
            }
            finally { reader.Finish(); }
        }));
        tests.Add(("Legacy panel settings stay opaque and appearance settings round-trip with validated limits", () =>
        {
            var legacy = JsonSerializer.Deserialize<PanelLayout>("{\"Width\":420,\"Height\":640,\"FontSize\":24}", Configuration.Json)!;
            Check(legacy.BackgroundOpacity == 1 && legacy.FontSize == 24 && legacy.Theme == "ink" && legacy.Accent == "" && legacy.CustomBase == "" && legacy.CustomInk == "");
            var config = new Configuration { Panel = legacy with { BackgroundOpacity = .55 } }; config.Validate();
            var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config, Configuration.Json), Configuration.Json)!;
            Check(restored.Panel == config.Panel);
            foreach (var opacity in new[] { .1, 1.1, double.NaN })
                Reject("panel_layout_invalid", () => (config with { Panel = config.Panel with { BackgroundOpacity = opacity } }).Validate());
            Reject("panel_layout_invalid", () => (config with { Panel = config.Panel with { Theme = "neon" } }).Validate());
            Reject("panel_layout_invalid", () => (config with { Panel = config.Panel with { Accent = "#12" } }).Validate());
            Reject("panel_layout_invalid", () => (config with { Panel = config.Panel with { CustomBase = "#FFFFFF", CustomInk = "#EEEEEE" } }).Validate());
            (config with { Panel = config.Panel with { CustomBase = "#101216", CustomInk = "#F4F6F8", Accent = "#8AB4F8" } }).Validate();
            foreach (var id in Theme.Ids)
            {
                Check(Theme.TryResolve(id, "", "", "", out var palette, out _));
                Check(Theme.Contrast(palette.Base, palette.Ink) >= 4.5);
            }
            Check(Theme.TryResolve(Theme.InkId, "", "", "", out var ink, out _) && ink.OnAccent == Theme.DarkInk);
            Check(Theme.TryResolve(Theme.PaperId, "", "", "", out var paper, out _) && paper.OnAccent == Theme.LightInk);
            Check(Theme.TryResolve(Theme.InkId, "#7DCEA0", "#101216", "#F4F6F8", out var mixed, out _) && mixed.Accent == Color.FromRgb(0x7D, 0xCE, 0xA0) && mixed.Base == Color.FromRgb(0x10, 0x12, 0x16));
        }));
        tests.Add(("Sending without a model keeps settings closed and preserves the input queue with an actionable notice", () =>
        {
            var client = new WorkspaceBackend();
            var window = new MainWindow(new Configuration { Backends = [], SelectedBackendId = "" }, client);
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var reader = window.WorkspaceWindow; reader.Show(); Pump();
                window.QueueText("synthetic clipboard input without a model");
                Check(((Button)reader.FindName("SendButton")).IsEnabled && !window.IsVisible);
                var shown = 0; window.IsVisibleChanged += (_, _) => { if (window.IsVisible) shown++; };
                window.RunCommand("send").GetAwaiter().GetResult(); Pump();
                Check(!window.IsVisible && shown == 0 && reader.Check());
                Check(window.QueuedText() == "synthetic clipboard input without a model" && client.Requests.Count == 0);
                var status = ((TextBlock)reader.FindName("StateText")).Text;
                Check(status.Contains("设置") && status.Contains("点击设置窗口") && !status.Contains("会切换焦点"));
                window.RunCommand("settings").GetAwaiter().GetResult(); Pump();
                Check(window.IsVisible && window.QueuedText() == "synthetic clipboard input without a model");
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Schema 2 adds recovery shortcut without replacing an existing scene binding", () =>
        {
            var config = new Configuration { Hotkeys = HotkeyDefaults.All.Where(k => k.Action != "through").ToImmutableArray() };
            config = config with { Scenes = config.Scenes.SetItem(0, config.Scenes[0] with { Trigger = new("scene:coding", "Ctrl+Alt+E") }) };
            var migrated = config.Migrate(); migrated.Validate();
            Check(migrated.Scenes[0].Trigger.Enabled && !migrated.Hotkeys.Single(k => k.Action == "through").Enabled);
        }));
        tests.Add(("Workspace input freezes encoded image and supplement before scene routing", () =>
        {
            var png = ImageProcessor.Encode(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8));
            var draft = new WorkspaceDraft();
            var input = new InputSnapshot(Guid.NewGuid(), InputKind.Screen, png, 42, DateTimeOffset.UtcNow);
            draft.Set(input, "Synthetic capture");
            var frozen = draft.Freeze("Use Python", 99);
            draft.Set(input with { Content = Payload.FromText("later clipboard") }, "changed"); draft.Clear();
            Check(frozen.Content.HasImage && frozen.Content.Text == "Use Python" && frozen.ClipboardSequence == 42 && draft.Input is null);
            var b = new Backend("v", "Vision", "https://example.test/v1/chat/completions", "v", "", SupportsImages: true);
            var settings = new Configuration { Backends = [b], SelectedBackendId = b.Id };
            var request = SceneRequestFactory.Prepare(settings, settings.Scenes[1], frozen);
            var pixels = new byte[16]; ImageProcessor.Decode(request.Content.Png).CopyPixels(pixels, 8, 0);
            Check(pixels[0] == 255 && request.Content.Text!.Contains("Use Python") && request.SceneId == "algorithm");
            var manual = draft.Freeze("manual only", 77);
            Check(manual.Kind == InputKind.ManualText && !manual.Content.HasImage && manual.Content.Text == "manual only");
        }));
        tests.Add(("Unified queue appends text and images, removes exactly one item and freezes every retained input", () =>
        {
            var draft = new WorkspaceDraft();
            InputSnapshot Entry(Payload content, uint sequence) => new(Guid.NewGuid(), content.HasImage ? InputKind.Screen : InputKind.ClipboardText, content, sequence, DateTimeOffset.UtcNow);
            var first = Entry(Payload.FromText("first clipboard"), 1); var second = Entry(Payload.FromText("second clipboard"), 2);
            var image = new Payload(null, [1, 2, 3], 2, 2);
            draft.Add(first, "text"); draft.Add(second, "text"); draft.Add(Entry(image, 3), "screen"); draft.Add(Entry(image, 4), "region");
            var frozen = draft.Freeze("supplement", 99);
            Check(draft.Items.Count == 4 && frozen.Content.Images.Count() == 2 && frozen.ClipboardSequence == 1);
            Check(frozen.Content.Text!.Contains("first clipboard") && frozen.Content.Text.Contains("second clipboard") && frozen.Content.Text.Contains("supplement"));
            draft.Remove(first.Id);
            Check(draft.Items.Count == 3 && !draft.Freeze("", 99).Content.Text!.Contains("first clipboard"));
            for (var i = 0; i < 3; i++) draft.Add(Entry(image, 5), "screen");
            var count = draft.Items.Count; Reject("image_queue_full", () => draft.Add(Entry(image, 6), "screen")); Check(draft.Items.Count == count);
            while (draft.Items.Count < WorkspaceDraft.MaximumItems) draft.Add(Entry(Payload.FromText("text"), 7), "text");
            Reject("input_queue_full", () => draft.Add(Entry(Payload.FromText("extra"), 8), "text"));
            draft.Clear(); Check(draft.Input is null && !draft.HasInput(""));
            Check(frozen.Content.Images.Count() == 2 && frozen.Content.Text.Contains("first clipboard"));
        }));
        tests.Add(("Two-point selection normalizes reversed physical coordinates and rejects cross-monitor or tiny bounds", () =>
        {
            var monitors = new[] { new CaptureTarget(CaptureKind.Monitor, "left", new(-1920, 0, 1920, 1080), "left"),
                new CaptureTarget(CaptureKind.Monitor, "right", new(0, 0, 2560, 1440), "right") };
            var region = CaptureService.FromCorners(new() { X = -100, Y = 600 }, new() { X = -800, Y = 100 }, monitors);
            Check(region.Bounds == new PixelRect(-800, 100, 700, 500) && region.MonitorBounds == monitors[0].Bounds);
            Reject("capture_cross_monitor", () => CaptureService.FromCorners(new() { X = -10, Y = 20 }, new() { X = 20, Y = 50 }, monitors));
            Reject("capture_region_too_small", () => CaptureService.FromCorners(new() { X = 10, Y = 20 }, new() { X = 12, Y = 23 }, monitors));
            var clicks = new CornerClicks();
            Check(!clicks.Process(false, new()));
            Check(clicks.Process(true, new() { X = 10, Y = 10 }) && !clicks.Complete);
            Check(clicks.Process(false, new() { X = 10, Y = 10 }) && !clicks.Complete);
            Check(clicks.Process(true, new() { X = 20, Y = 20 }) && !clicks.Complete);
            Check(clicks.Process(false, new() { X = 20, Y = 20 }) && clicks.Complete);
            Check(!clicks.Process(true, new()));
        }));
        tests.Add(("Every queued image reaches the production HTTP adapter unchanged", () =>
        {
            using var endpoint = new SyntheticEndpoint(); using var client = new ChatCompletionsClient(endpoint);
            var image = ImageProcessor.Encode(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8));
            var b = new Backend("test", "Synthetic", endpoint.Url, "vision", "", SupportsImages: true, AllowLoopbackHttp: true, Stream: false);
            var config = new Configuration { Backends = [b], SelectedBackendId = b.Id };
            var draft = new WorkspaceDraft();
            for (var i = 0; i < 2; i++) draft.Add(new(Guid.NewGuid(), InputKind.Screen, image, 1, DateTimeOffset.UtcNow), "screen");
            var request = SceneRequestFactory.Prepare(config, config.Scenes[1], draft.Freeze("both pictures", 1));
            foreach (var part in request.Content.Images)
            {
                var pixels = new byte[16]; ImageProcessor.Decode(part.Png).CopyPixels(pixels, 8, 0); Check(pixels[0] == 255 && pixels[3] == 255);
            }
            async Task Send() { await foreach (var item in client.SendAsync(request, CancellationToken.None)) { } }
            Send().GetAwaiter().GetResult();
            var content = endpoint.Requests.Single().GetProperty("messages")[1].GetProperty("content");
            var images = content.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "image_url").ToArray();
            Check(images.Length == 2);
            foreach (var part in images)
            {
                var uri = part.GetProperty("image_url").GetProperty("url").GetString()!;
                var bytes = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]).ToImmutableArray();
                var pixels = new byte[16]; ImageProcessor.Decode(bytes).CopyPixels(pixels, 8, 0); Check(pixels[0] == 255);
            }
        }));
        tests.Add(("Streaming keeps document and committed paragraphs and preserves manual reading position", () =>
        {
            var answer = new AnswerView { FontSize = 16 };
            var surface = new Border { Width = 500, Height = 260, Child = answer };
            void Layout() { surface.Measure(new Size(500, 260)); surface.Arrange(new Rect(0, 0, 500, 260)); surface.UpdateLayout(); Pump(); surface.UpdateLayout(); }
            Layout();
            var text = string.Join('\n', Enumerable.Range(0, 120).Select(n => $"Synthetic line {n}")) + "\n";
            answer.Render(text); Layout();
            var doc = answer.Reader.Document; var first = doc.Blocks.FirstBlock;
            Check(answer.Scroll is { ScrollableHeight: > 0 } && answer.Following);
            answer.Page(false); Layout(); var offset = answer.Scroll!.VerticalOffset;
            answer.Render(text + "```python\nprint('literal <script>')\n"); Layout();
            Check(ReferenceEquals(doc, answer.Reader.Document) && ReferenceEquals(first, doc.Blocks.FirstBlock));
            Check(!answer.Following && Math.Abs(answer.Scroll.VerticalOffset - offset) < 2);
            Check(new TextRange(doc.ContentStart, doc.ContentEnd).Text.Contains("literal <script>"));
            answer.Follow(); Layout(); Check(answer.Following && answer.Scroll.VerticalOffset >= answer.Scroll.ScrollableHeight - 2);
            answer.Clear(); Layout(); Check(answer.Text == "" && answer.Reader.Document.Blocks.Count == 0);
        }));
        tests.Add(("Production workspace sends selected scene with queued material, stops without loss, and clears a new task", () =>
        {
            var client = new WorkspaceBackend();
            var b = new Backend("test", "Synthetic", "https://example.test/v1/chat/completions", "base", "");
            var config = new Configuration { Backends = [b], SelectedBackendId = b.Id };
            var window = new MainWindow(config, client);
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var reader = window.WorkspaceWindow;
                reader.Show(); Pump();
                var picker = (ComboBox)reader.FindName("WorkspaceScene");
                Check(reader.FindName("DraftText") is null && reader.FindName("InteractionButton") is null && picker.IsEnabled && !picker.Focusable);
                ((ComboBox)reader.FindName("WorkspaceScene")).SelectedValue = "algorithm";
                window.QueueText("only visible manual input");
                var pending = window.RunCommand("send");
                Until(() => client.Requests.Count == 1 || pending.IsCompleted);
                if (client.Requests.Count != 1)
                {
                    Console.WriteLine($"Synthetic send ended: mode={window.WorkspaceCoordinator.Mode}, status={((TextBlock)window.FindName("StatusLabel")).Text}, code={((TextBlock)window.FindName("ErrorCode")).Text}");
                    throw new Exception("Synthetic request did not reach backend");
                }
                Check(client.Requests[0].SceneId == "algorithm" && client.Requests[0].Content.Text!.Contains("only visible manual input"));
                Check(client.Requests[0].Snapshot.Kind == InputKind.ClipboardText);
                Check(!((Button)reader.FindName("SendButton")).IsEnabled && ((Button)reader.FindName("StopButton")).IsEnabled);
                Check(!picker.IsEnabled);
                client.Events.Writer.TryWrite(new(Thinking: true));
                Until(() => window.WorkspaceCoordinator.State == RequestState.Thinking);
                Check(!((Button)reader.FindName("SendButton")).IsEnabled && ((Button)reader.FindName("StopButton")).IsEnabled);
                client.Events.Writer.TryWrite(new("synthetic partial"));
                Until(() => window.WorkspaceCoordinator.Latest?.Text == "synthetic partial");
                window.RunCommand("cancel").GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.Latest is { State: RequestState.Cancelled, Text: "synthetic partial" });
                Check(((AnswerView)reader.FindName("Answer")).Text == "synthetic partial");
                Check(Equals(((Button)reader.FindName("CopyButton")).Content, "复制部分回答"));
                client.Events.Writer.TryWrite(new("late text")); client.Events.Writer.TryWrite(new(Completed: true)); client.Events.Writer.Complete();
                Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.Latest!.Text == "synthetic partial");
                window.RunCommand("newTask").GetAwaiter().GetResult();
                Check(window.QueuedText() == "" && window.WorkspaceCoordinator.Latest is null && ((AnswerView)reader.FindName("Answer")).Text == "");
                Check(!((Button)reader.FindName("CopyButton")).IsEnabled && !((Button)reader.FindName("SendButton")).IsEnabled);
                window.QueueText("must clear when protection is lost");
                Check(Native.SetWindowDisplayAffinity(new WindowInteropHelper(reader).Handle, 0));
                window.RunCommand("send").GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.Mode == RunState.ProtectionUnavailable && client.Requests.Count == 1 && window.QueuedText() == "");
            }
            finally { window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
        }));
        tests.Add(("Production queue sends retained text and images together and starts fresh after an answer", () =>
        {
            var client = new WorkspaceBackend();
            var b = new Backend("test", "Synthetic", "https://example.test/v1/chat/completions", "vision", "", SupportsImages: true);
            var config = new Configuration { Backends = [b], SelectedBackendId = b.Id };
            var window = new MainWindow(config, client);
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var reader = window.WorkspaceWindow; reader.Show(); Pump();
                InputSnapshot Entry(Payload payload) => new(Guid.NewGuid(), payload.HasImage ? InputKind.Screen : InputKind.ClipboardText, payload, 1, DateTimeOffset.UtcNow);
                var removed = Entry(Payload.FromText("removed text"));
                window.QueueSnapshot(removed, "clipboard");
                window.QueueSnapshot(Entry(Payload.FromText("retained text")), "clipboard");
                var image = ImageProcessor.Encode(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8));
                window.QueueSnapshot(Entry(image), "screen"); window.QueueSnapshot(Entry(image), "region");
                window.RunCommand("removeInput:" + removed.Id).GetAwaiter().GetResult();
                window.QueueText("supplement");
                var pending = window.RunCommand("send");
                Until(() => client.Requests.Count == 1 || pending.IsCompleted);
                var request = client.Requests.Single();
                Check(request.Content.Text!.Contains("retained text") && request.Content.Text.Contains("supplement") && !request.Content.Text.Contains("removed text"));
                Check(request.Content.Images.Count() == 2);
                foreach (var part in request.Content.Images)
                {
                    var pixels = new byte[16]; ImageProcessor.Decode(part.Png).CopyPixels(pixels, 8, 0); Check(pixels[0] == 255 && pixels[3] == 255);
                }
                client.Events.Writer.TryWrite(new("complete answer")); client.Events.Writer.TryWrite(new(Completed: true)); client.Events.Writer.Complete();
                Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.Latest?.State == RequestState.Completed);
                window.QueueSnapshot(Entry(Payload.FromText("next task")), "clipboard");
                Check(window.QueuedText() == "next task" && window.WorkspaceCoordinator.Latest is null);
                var tiles = ((ItemsControl)reader.FindName("InputQueue")).Items;
                Check(tiles.Count == 1);
                window.RunCommand("deleteCapture").GetAwaiter().GetResult();
                Check(((ItemsControl)reader.FindName("InputQueue")).Items.Count == 0 && !((Button)reader.FindName("SendButton")).IsEnabled);
            }
            finally { window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
        }));
        tests.Add(("Reasoning token exhaustion is visible in the floating answer area and never offers a completed copy", () =>
        {
            var client = new WorkspaceBackend();
            var b = new Backend("test", "Synthetic", DeepSeekSetup.Endpoint, "reasoning", "", MaxTokens: 4096);
            var window = new MainWindow(new Configuration { Backends = [b], SelectedBackendId = b.Id }, client);
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                new WindowInteropHelper(window).EnsureHandle(); var reader = window.WorkspaceWindow; reader.Show(); Pump();
                window.QueueText("synthetic algorithm"); var pending = window.RunCommand("send"); Until(() => client.Requests.Count == 1);
                client.Events.Writer.TryWrite(new(Thinking: true)); Until(() => window.WorkspaceCoordinator.State == RequestState.Thinking);
                Check(((TextBlock)window.FindName("StatusLabel")).Text.Contains("模型正在思考"));
                client.Events.Writer.Complete(new SafeFailure("response_token_limit")); Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.Latest is { State: RequestState.Failed, Text: "", Code: "response_token_limit" });
                Check(((TextBlock)reader.FindName("EmptyAnswer")).Text.Contains("token 已用尽"));
                Check(((TextBlock)reader.FindName("StateText")).Text.Contains("提高上限后重新发送"));
                Check(!((Button)reader.FindName("CopyButton")).IsEnabled && ((Button)reader.FindName("SendButton")).IsEnabled);
                Check(window.QueuedText() == "synthetic algorithm");
                var recoveryButton = (Button)reader.FindName("DirectRetryButton");
                Check(recoveryButton.Visibility == Visibility.Visible && recoveryButton.Content.ToString()!.Contains("16K"));
                var original = client.Requests.Single();
                var recovery = window.RunCommand("retryDirect"); Until(() => client.Requests.Count == 2);
                var recovered = client.Requests[1];
                Check(recovered.Content == original.Content && recovered.Snapshot == original.Snapshot);
                Check(recovered.Backend == original.Backend with { ThinkingMode = ThinkingMode.Disabled, MaxTokens = 16384 });
                client.Events.Writer.TryWrite(new("synthetic complete answer")); client.Events.Writer.TryWrite(new(Completed: true)); client.Events.Writer.Complete();
                Until(() => recovery.IsCompleted); recovery.GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.State == RequestState.Completed && recoveryButton.Visibility == Visibility.Collapsed);
            }
            finally { window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
        }));
        tests.Add(("Control center visibility keeps startup ready without automatically resuming explicit pause", () =>
        {
            var client = new WorkspaceBackend();
            var b = new Backend("test", "Synthetic", "https://example.test/v1/chat/completions", "base", "");
            var window = new MainWindow(new Configuration { Backends = [b], SelectedBackendId = b.Id }, client);
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    window.Show(); Pump(); window.WorkspaceWindow.Show(); window.Hide(); Pump();
                    Check(window.WorkspaceCoordinator.Mode == RunState.Ready && window.WorkspaceWindow.FindName("InteractionButton") is null);
                    Check(((Button)window.WorkspaceWindow.FindName("ClipboardButton")).IsEnabled);
                    Check(Native.GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, out var affinity) && affinity == 0x11);
                }
                Check(client.Requests.Count == 0);
                window.RunCommand("pause").GetAwaiter().GetResult();
                window.Show(); Pump(); window.Hide(); Pump();
                Check(window.WorkspaceCoordinator.Mode == RunState.Paused && window.WorkspaceWindow.FindName("DraftText") is null);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Floating workspace renders narrow and normal layouts and clears all queued material and output text", () =>
        {
            var reader = new ReaderWindow();
            new WindowInteropHelper(reader).EnsureHandle();
            reader.Configure(SceneDefaults.All, "algorithm", "Synthetic / test-model", "Ctrl+Alt+E");
            reader.SetShortcutHints(HotkeyDefaults.All.ToDictionary(k => k.Action, k => ShortcutLabel.Describe(k, true)));
            reader.SetActions(true, false, true, true, false, true);
            var queue = new WorkspaceDraft();
            queue.Add(new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText("输入约束：数组长度不超过 100000，保留元素原始顺序。"), 1, DateTimeOffset.UtcNow), "剪贴板文本");
            var thumbnail = ImageProcessor.Encode(BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)230, 80 * 40 * 4).ToArray(), 80 * 4));
            queue.Add(new(Guid.NewGuid(), InputKind.Screen, thumbnail, 1, DateTimeOffset.UtcNow), "两点截图");
            reader.SetInputs(queue.Items);
            var answer = (AnswerView)reader.FindName("Answer");
            reader.Render("使用集合记录已出现的元素。\n\n```python\ndef unique(values):\n    return list(dict.fromkeys(values))\n```\n平均时间复杂度 O(n)，空间复杂度 O(n)。", "已完成 · 每次独立请求");
            ((TextBlock)reader.FindName("EmptyAnswer")).Visibility = Visibility.Collapsed;
            reader.Status("已完成 · 每次独立请求");
            var root = (FrameworkElement)reader.Content; reader.Content = null;
            var surface = new Border { Background = reader.Background, Child = root, Resources = reader.Resources };
            TextElement.SetForeground(surface, reader.Foreground); TextElement.SetFontFamily(surface, reader.FontFamily); TextElement.SetFontSize(surface, 14);
            foreach (var (width, height) in new[] { (600, 740), (360, 740), (360, 480) })
            {
                surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout(); Pump();
                Check(answer.ActualHeight > (height == 480 ? 160 : 350) && ((Button)reader.FindName("SendButton")).IsEnabled);
                Check(!((Expander)reader.FindName("ComposerExpander")).IsExpanded);
                var index = Array.IndexOf(args, "--render-dir");
                if (index >= 0 && index + 1 < args.Length)
                {
                    Directory.CreateDirectory(args[index + 1]);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(args[index + 1], height == 740 ? $"workspace-{width}.png" : $"workspace-{width}-{height}.png")); encoder.Save(file);
                }
            }
            ((Expander)reader.FindName("ComposerExpander")).IsExpanded = true;
            surface.Measure(new Size(360, 480)); surface.Arrange(new Rect(0, 0, 360, 480)); surface.UpdateLayout(); Pump();
            Check(answer.ActualHeight > 75 && ((ItemsControl)reader.FindName("InputQueue")).Items.Count == 2);
            reader.Render(answer.Text + "\nnext chunk", "生成中");
            Check(((Expander)reader.FindName("ComposerExpander")).IsExpanded); // A stream must not undo an explicit expansion.
            reader.SetDirectRetry(16384);
            reader.Render("", ConnectionDiagnostics.Explain("response_token_limit"));
            surface.Measure(new Size(360, 740)); surface.Arrange(new Rect(0, 0, 360, 740)); surface.UpdateLayout(); Pump();
            var recovery = (Button)reader.FindName("DirectRetryButton");
            Check(recovery.Visibility == Visibility.Visible && recovery.ActualHeight > 0 && recovery.TranslatePoint(new Point(0, recovery.ActualHeight), surface).Y <= 724);
            var renderIndex = Array.IndexOf(args, "--render-dir");
            if (renderIndex >= 0 && renderIndex + 1 < args.Length)
            {
                reader.SetDirectRetry(null); reader.ClearAnswer(); reader.Render("合成回答：回答区应清晰可读。\n\n```python\nprint('readable workspace')\n```", "已完成");
                reader.ApplyLayout(reader.Layout() with { BackgroundOpacity = .85, FontSize = 24 });
                surface.Measure(new Size(600, 740)); surface.Arrange(new Rect(0, 0, 600, 740)); surface.UpdateLayout(); Pump();
                var bitmap = new RenderTargetBitmap(600, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[renderIndex + 1], "workspace-appearance.png")); encoder.Save(output);
                reader.ApplyLayout(reader.Layout() with { BackgroundOpacity = 1, FontSize = 18 });
            }
            reader.Clear(); Check(answer.Text == "" && ((ItemsControl)reader.FindName("InputQueue")).ItemsSource is null);
        }));
    }

    private sealed class WorkspaceBackend : IBackendClient
    {
        public List<PreparedRequest> Requests { get; } = [];
        public Channel<BackendEvent> Events { get; private set; } = Channel.CreateUnbounded<BackendEvent>();
        public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Requests.Count > 0) Events = Channel.CreateUnbounded<BackendEvent>();
            Requests.Add(request);
            // Intentionally ignores cancellation to verify the production workspace's generation guard.
            await foreach (var item in Events.Reader.ReadAllAsync()) yield return item;
        }
    }
}
