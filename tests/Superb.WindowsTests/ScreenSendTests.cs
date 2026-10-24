using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Superb.Core;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class ScreenSendTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Screen send assertion failed"); }
    private static Backend Vision => new("vision", "Synthetic vision", "https://example.test/chat/completions", "synthetic", "", SupportsImages: true);
    private static Configuration Settings(Backend? backend = null) => new()
    {
        Backends = [backend ?? Vision], SelectedBackendId = (backend ?? Vision).Id,
        ClearOwnedClipboardOnPause = false
    };
    private static Payload Picture(byte shade = 255) => ImageProcessor.Encode(BitmapSource.Create(2, 2, 96, 96,
        PixelFormats.Bgra32, null, Enumerable.Repeat(shade, 16).ToArray(), 8));
    private static InputSnapshot Entry(Payload payload) => new(Guid.NewGuid(),
        payload.HasImage ? InputKind.Screen : InputKind.ClipboardText, payload, 0, DateTimeOffset.UtcNow);
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Check(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    internal static void Add(List<(string, Action)> tests)
    {
        tests.Add(("Capture-and-send starts a screenshot-only task, keeps the selected scene and rejects duplicate triggers", () =>
        {
            var capture = new TaskCompletionSource<Payload>();
            using var host = new Host(Settings(), _ => capture.Task);
            host.Window.Show();
            host.Window.QueueSnapshot(Entry(Payload.FromText("queued text")), "text");
            host.Window.QueueSnapshot(Entry(Picture(80)), "previous image");
            ((ComboBox)host.Reader.FindName("WorkspaceScene")).SelectedValue = "algorithm";
            var pending = host.Window.RunCommand("captureScreenSend");
            Check(host.Captures == 1 && !host.Reader.IsVisible && !host.Window.IsVisible);
            Check(!((Button)host.Reader.FindName("ScreenSendButton")).IsEnabled);
            Wait(host.Window.RunCommand("captureScreenSend"));
            Check(host.Captures == 1 && host.Client.Requests.Count == 0);
            var screenshot = Picture();
            capture.SetResult(screenshot); Wait(pending);
            var request = host.Client.Requests.Single();
            Check(request.SceneId == "algorithm" && request.Backend == Vision);
            Check(string.IsNullOrEmpty(request.Content.Text));
            Check(request.Content.Images.Count() == 1 && host.Queue.Count == 1);
            Check(request.Content.Png.AsSpan().SequenceEqual(screenshot.Png.AsSpan()));
            Check(host.Reader.IsVisible && host.Window.WorkspaceCoordinator.State == RequestState.Completed);
            Check(((Button)host.Reader.FindName("ScreenSendButton")).IsVisible);
            var send = (Button)host.Reader.FindName("SendButton");
            var screenSend = (Button)host.Reader.FindName("ScreenSendButton");
            var toolbar = (Panel)VisualTreeHelper.GetParent(send);
            Check(toolbar.Children.IndexOf(screenSend) == toolbar.Children.IndexOf(send) + 1);
        }));
        tests.Add(("Failed screenshot and rejected screenshot size preserve draft and queue without sending", () =>
        {
            foreach (var failedCapture in new[] { true, false })
            {
                using var host = new Host(Settings(failedCapture ? Vision : Vision with { MaxInputBytes = 1 }),
                    _ => failedCapture ? Task.FromException<Payload>(new SafeFailure("capture_unavailable")) : Task.FromResult(Picture()));
                host.Window.QueueSnapshot(Entry(Payload.FromText("keep input")), "text");
                Wait(host.Window.RunCommand("captureScreenSend"));
                Check(host.Client.Requests.Count == 0 && host.Captures == 1 && host.Queue.Count == 1);
                Check(host.Window.QueuedText() == "keep input" && host.Reader.IsVisible);
            }
        }));
        tests.Add(("Cancelled, cleared, paused and unprotected captures cannot send late screenshot data", () =>
        {
            foreach (var action in new[] { "cancel", "newTask", "pause", "loseProtection" })
            {
                var capture = new TaskCompletionSource<Payload>();
                using var host = new Host(Settings(), _ => capture.Task);
                host.Window.QueueSnapshot(Entry(Payload.FromText("existing input")), "text");
                var pending = host.Window.RunCommand("captureScreenSend");
                Check(host.Captures == 1);
                if (action == "loseProtection")
                    Check(Native.SetWindowDisplayAffinity(new WindowInteropHelper(host.Reader).Handle, 0));
                else Wait(host.Window.RunCommand(action));
                capture.SetResult(Picture()); Wait(pending);
                Check(host.Client.Requests.Count == 0);
                if (action == "cancel") Check(host.Queue.Count == 1 && host.Window.QueuedText() == "existing input" && host.Reader.IsVisible);
                else Check(host.Queue.Count == 0);
            }
        }));
        tests.Add(("Screen send checks the recipient before capture and replaces full queues with only the new screenshot", () =>
        {
            foreach (var config in new[] { Settings(Vision with { SupportsImages = false }), new Configuration() })
            {
                using var host = new Host(config, _ => Task.FromResult(Picture()));
                Wait(host.Window.RunCommand("captureScreenSend"));
                Check(host.Captures == 0 && host.Client.Requests.Count == 0);
            }
            using var full = new Host(Settings(), _ => Task.FromResult(Picture()));
            for (var i = 0; i < Policy.MaximumImages; i++) full.Window.QueueSnapshot(Entry(Picture()), "image");
            Wait(full.Window.RunCommand("captureScreenSend"));
            Check(full.Captures == 1 && full.Client.Requests.Count == 1 && full.Queue.Count == 1);
            Check(full.Client.Requests.Single().Content.Images.Count() == 1);
        }));
        tests.Add(("Screen send after an answer starts fresh, retry reuses its image, and ordinary capture still only queues", () =>
        {
            using var host = new Host(Settings(), _ => Task.FromResult(Picture()));
            host.Window.QueueText("previous question"); Wait(host.Window.RunCommand("send"));
            Wait(host.Window.RunCommand("captureScreenSend"));
            Check(host.Client.Requests.Count == 2 && host.Captures == 1);
            var request = host.Client.Requests[1];
            Check(request.Content.HasImage && string.IsNullOrEmpty(request.Content.Text));
            Check(host.Queue.Count == 1);
            Wait(host.Window.RunCommand("retry"));
            Check(host.Client.Requests.Count == 3 && host.Client.Requests[2] == request && host.Captures == 1);
            Wait(host.Window.RunCommand("captureScreen"));
            Check(host.Captures == 2 && host.Client.Requests.Count == 3 && host.Queue.Count == 1);
        }));
        tests.Add(("New screen-send shortcut migrates without replacing existing or conflicting bindings", () =>
        {
            var old = new Configuration { Hotkeys = HotkeyDefaults.All.Where(k => k.Action != "captureScreenSend").ToImmutableArray() };
            var migrated = old.Migrate(); migrated.Validate();
            var defaultKey = new Hotkey("captureScreenSend", "F4", HotkeyMode.Exclusive);
            Check(migrated.Hotkeys.Single(k => k.Action == "captureScreenSend") == defaultKey);
            Check(old.Hotkeys.All(migrated.Hotkeys.Contains));
            var scene = old.Scenes[0];
            var conflict = (old with { Scenes = old.Scenes.Replace(scene, scene with { Trigger = scene.Trigger with { Gesture = "F4", Mode = HotkeyMode.Exclusive } }) }).Migrate();
            conflict.Validate(); Check(!conflict.Hotkeys.Single(k => k.Action == "captureScreenSend").Enabled);
            var custom = migrated with { Hotkeys = migrated.Hotkeys.Select(k => k.Action == "captureScreenSend" ? k with { Gesture = "F16", Mode = HotkeyMode.Exclusive, Enabled = false } : k).ToImmutableArray() };
            Check(custom.Migrate().Hotkeys.SequenceEqual(custom.Hotkeys));
            var previous = old with { Hotkeys = old.Hotkeys.Add(new("captureScreenSend", "Ctrl+Alt+X")) };
            Check(previous.Migrate().Hotkeys.Single(k => k.Action == "captureScreenSend") == defaultKey);
            var disabled = old with { Hotkeys = old.Hotkeys.Add(new("captureScreenSend", "Ctrl+Alt+X", Enabled: false)) };
            Check(disabled.Migrate().Hotkeys.Single(k => k.Action == "captureScreenSend") == defaultKey with { Enabled = false });
            var previousConflict = (previous with { Scenes = conflict.Scenes }).Migrate();
            previousConflict.Validate(); Check(!previousConflict.Hotkeys.Single(k => k.Action == "captureScreenSend").Enabled);
            Check(!previous.Migrate().ExclusiveHotkeysEnabled);
            Check(ShortcutLabel.Describe(defaultKey, true).Active);
            Check(!ShortcutLabel.Describe(defaultKey with { Enabled = false }, true).Active);
            Check(ShortcutLabel.Describe(new("cancel", "F5", HotkeyMode.Exclusive), true).Active);
            Check(ShortcutLabel.Describe(defaultKey, false, "exclusive_registration_failed").Text == "未生效");
        }));
    }
    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? context = SynchronizationContext.Current;
        public MainWindow Window { get; }
        public ReaderWindow Reader => Window.WorkspaceWindow;
        public ItemCollection Queue => ((ItemsControl)Reader.FindName("InputQueue")).Items;
        public TestBackend Client { get; } = new();
        public int Captures { get; private set; }
        public Host(Configuration config, Func<CaptureTarget, Task<Payload>> capture)
        {
            MainWindow? window = null;
            window = new MainWindow(config, Client, target =>
            {
                Check(!window!.IsVisible && !window.WorkspaceWindow.IsVisible && target.Kind == CaptureKind.Monitor);
                Captures++; return capture(target);
            });
            Window = window;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Window.Dispatcher));
            new WindowInteropHelper(Window).EnsureHandle(); Reader.Show();
        }
        public void Dispose() { Window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
    }
    private sealed class TestBackend : IBackendClient
    {
        public List<PreparedRequest> Requests { get; } = [];
        public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new("synthetic answer"); yield return new(Completed: true);
        }
    }
}
