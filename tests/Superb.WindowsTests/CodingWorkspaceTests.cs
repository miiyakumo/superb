using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Reflection;
using Superb.Core;
using Superb.Core.Coding;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class CodingWorkspaceTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Coding workspace assertion failed"); }
    private static InputSnapshot Input(string text) => new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText(text), 0, DateTimeOffset.UtcNow);
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
        Check(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    internal static void Add(List<(string, Action)> tests, string[] args)
    {
        tests.Add(("Coding preserves independent material queues, accumulates grounded feedback and rejects duplicate-only reruns", () =>
        {
            using var host = new Host();
            host.Window.QueueSnapshot(Input("ordinary input"), "ordinary");
            host.Choose("coding");
            Check(host.QueueCount == 0);
            host.Window.QueueSnapshot(Input("Task requires Solve; current function is missing"), "task");
            Wait(host.Window.RunCommand("send"));
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 1 && host.Model.Requests.Count == 3);
            Check(host.Ordinary.Calls == 0 && host.QueueCount == 0);
            Check((string)host.Button("CopyButton").Content == "复制平台提示词");
            Check(host.Answer.Contains("下一步方向") && host.Answer.Contains("Return current Solve"));
            Check(!host.Button("RetryButton").IsEnabled);
            host.Window.QueueSnapshot(Input("Task requires Solve; current function is missing"), "duplicate");
            Wait(host.Window.RunCommand("send")); Check(host.Model.Requests.Count == 3);
            host.Window.QueueSnapshot(Input("Platform says done but returns no function"), "feedback");
            Wait(host.Window.RunCommand("send"));
            Check(host.Window.WorkspaceCodingSession.Observations.Count == 2);
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 2);
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts[0].Assessment?.Outcome == AttemptOutcome.Claimed);
            host.Choose("general");
            Check(host.QueueCount == 1);
            Wait(host.Window.RunCommand("send")); Check(host.Ordinary.Calls == 1);
            host.Choose("coding"); Check(host.Answer.Contains("Return current Solve"));
            Wait(host.Window.RunCommand("newTask"));
            Check(host.Window.WorkspaceCodingSession.Observations.Count == 0 && host.Window.WorkspaceCodingSession.Memory.Attempts.IsEmpty);
            host.Choose("general"); Check(host.QueueCount == 1 && host.Window.WorkspaceCoordinator.State == RequestState.Completed && host.Answer == "ordinary answer");
        }));
        tests.Add(("Coding queues new feedback during analysis without changing the frozen run", () =>
        {
            using var host = new Host(); host.Choose("coding");
            host.Model.Gate = new(); host.Window.QueueSnapshot(Input("first material"), "first");
            var task = host.Window.RunCommand("send");
            Check(host.Window.WorkspaceCodingRunner.IsRunning && host.Button("ClipboardButton").IsEnabled);
            Check(!host.Button("ClipboardAnalyzeButton").IsEnabled && !host.Button("SendButton").IsEnabled);
            host.Window.QueueSnapshot(Input("later feedback"), "later");
            Check(host.QueueCount == 1 && host.Window.WorkspaceCodingSession.Observations.Count == 1);
            host.Model.Gate.SetResult(); Wait(task);
            Check(host.Model.Requests.All(r => !r.Content.Text!.Contains("later feedback")));
            Check(host.QueueCount == 1 && host.Button("SendButton").IsEnabled);
            Check(((TextBlock)host.Reader.FindName("CodingPending")).Text.Contains("尚未纳入"));
            host.Model.Gate = null; Wait(host.Window.RunCommand("send"));
            Check(host.Window.WorkspaceCodingSession.Observations.Count == 2 && host.QueueCount == 0);
        }));
        tests.Add(("Coding stop, switch and pause reject late model results and clear sensitive records", () =>
        {
            foreach (var action in new[] { "cancel", "pause", "newTask", "switch" })
            {
                using var host = new Host(); host.Choose("coding"); host.Model.Gate = new();
                host.Window.QueueSnapshot(Input("sensitive source"), "source"); var task = host.Window.RunCommand("send");
                if (action == "switch") host.Choose("general"); else Wait(host.Window.RunCommand(action));
                Check(!host.Window.WorkspaceCodingRunner.IsRunning);
                host.Model.Gate.SetResult(); Wait(task);
                Check(host.Window.WorkspaceCodingSession.Memory.Attempts.IsEmpty);
                if (action is "pause" or "newTask")
                {
                    Check(host.Window.WorkspaceCodingSession.Observations.Count == 0);
                    Check(host.Window.WorkspaceCodingRunner.Latest is null && !host.Window.WorkspaceCodingRunner.CanRetry);
                    Check(((TextBlock)host.Reader.FindName("CodingHistory")).Text == "");
                }
                else Check(host.Window.WorkspaceCodingSession.Observations.Count == 1 && host.Window.WorkspaceCodingRunner.CanRetry);
            }
        }));
        tests.Add(("Coding retry preserves frozen material and options and keeps new feedback pending", () =>
        {
            using var host = new Host(); host.Choose("coding"); host.Model.FailNext = true;
            host.Window.QueueSnapshot(Input("frozen original"), "original"); Wait(host.Window.RunCommand("send"));
            Check(host.Window.WorkspaceCodingRunner.CanRetry);
            host.Window.QueueSnapshot(Input("new pending feedback"), "pending");
            ((ComboBox)host.Reader.FindName("CodingProtocol")).SelectedIndex = 1;
            Wait(host.Window.RunCommand("retry"));
            Check(host.Model.Requests.All(r => r.Protocol == AgentProtocol.JsonActions));
            Check(host.Model.Requests.All(r => !r.Content.Text!.Contains("new pending feedback")));
            Check(host.QueueCount == 1 && host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 1);
            Check(!host.Window.WorkspaceCodingRunner.CanRetry);
            Wait(host.Window.RunCommand("retry")); Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 1);
        }));
        tests.Add(("Coding screen analysis keeps memory and the recipient and protocol frozen before asynchronous capture", () =>
        {
            var captured = new TaskCompletionSource<Payload>();
            using var host = new Host(_ => captured.Task); host.Choose("coding");
            host.Window.QueueSnapshot(Input("original task"), "task"); Wait(host.Window.RunCommand("send"));
            var pending = host.Window.RunCommand("captureScreenSend");
            var field = typeof(MainWindow).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var saved = (Configuration)field.GetValue(host.Window)!;
            var other = saved.Backends[0] with { Id = "other", Model = "other-model" };
            field.SetValue(host.Window, saved with { Backends = saved.Backends.Add(other), SelectedBackendId = other.Id });
            ((ComboBox)host.Reader.FindName("CodingProtocol")).SelectedIndex = 1;
            captured.SetResult(ImageProcessor.Encode(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
            Wait(pending);
            Check(host.Window.WorkspaceCodingSession.Observations.Count == 2);
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 2 && host.Ordinary.Calls == 0);
            Check(host.Model.Requests.All(r => r.Backend.Id == "test" && r.Protocol == AgentProtocol.JsonActions));
            Check(((TextBlock)host.Reader.FindName("ReceiverText")).Text.Contains("本次：test / test"));
        }));
        tests.Add(("Failed Coding analysis can use changed options with retained evidence through Analyze", () =>
        {
            using var host = new Host(); host.Choose("coding"); host.Model.FailNext = true;
            host.Window.QueueSnapshot(Input("retained evidence"), "source"); Wait(host.Window.RunCommand("send"));
            Check(host.QueueCount == 0 && host.Button("SendButton").IsEnabled);
            ((ComboBox)host.Reader.FindName("CodingProtocol")).SelectedIndex = 1;
            ((ComboBox)host.Reader.FindName("CodingCalls")).SelectedIndex = 2;
            Wait(host.Window.RunCommand("send"));
            Check(host.Model.Requests.Skip(1).All(r => r.Protocol == AgentProtocol.NativeTools));
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 1 && host.Window.WorkspaceCodingSession.Observations.Count == 1);
            Check(!host.Button("SendButton").IsEnabled);
        }));
        tests.Add(("Coding directive cards render at narrow and normal widths with usable copy and analysis controls", () =>
        {
            using var host = new Host(); host.Choose("coding");
            host.Window.QueueSnapshot(Input("Need current Solve and test feedback"), "task"); Wait(host.Window.RunCommand("send"));
            foreach (var width in new[] { 400, 600 })
            {
                host.Reader.Width = width; host.Reader.Height = 740;
                ((Expander)host.Reader.FindName("ComposerExpander")).IsExpanded = true;
                host.Reader.UpdateLayout();
                Check(host.Button("CopyButton").IsVisible && host.Button("ClipboardAnalyzeButton").IsVisible);
                var copy = host.Button("CopyButton").TransformToAncestor(host.Reader).Transform(new Point());
                Check(copy.Y >= 0 && copy.Y + host.Button("CopyButton").ActualHeight <= host.Reader.ActualHeight);
                var surface = (FrameworkElement)host.Reader.Content;
                var renderIndex = Array.IndexOf(args, "--render-dir");
                if (renderIndex >= 0 && renderIndex + 1 < args.Length)
                {
                    Directory.CreateDirectory(args[renderIndex + 1]);
                    var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(args[renderIndex + 1], $"coding-{width}.png")); encoder.Save(stream);
                }
            }
        }));
    }
    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? context = SynchronizationContext.Current;
        public MainWindow Window { get; }
        public ReaderWindow Reader => Window.WorkspaceWindow;
        public int QueueCount => ((ItemsControl)Reader.FindName("InputQueue")).Items.Count;
        public string Answer => ((AnswerView)Reader.FindName("Answer")).Text;
        public Model Model { get; } = new();
        public Ordinary Ordinary { get; } = new();
        public Host(Func<CaptureTarget, Task<Payload>>? capture = null)
        {
            var target = new Backend("test", "test", "https://example.test/chat/completions", "test", "", SupportsImages: true);
            var settings = new Configuration { Backends = [target], SelectedBackendId = target.Id, AutoCopy = false, ClearOwnedClipboardOnPause = false };
            Window = new MainWindow(settings, Ordinary, testCapture: capture, testAgent: Model);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Window.Dispatcher));
            new WindowInteropHelper(Window).EnsureHandle(); Reader.Show();
        }
        public Button Button(string name) => (Button)Reader.FindName(name);
        public void Choose(string id) => ((ComboBox)Reader.FindName("WorkspaceScene")).SelectedValue = id;
        public void Dispose() { Window.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }
    private sealed class Model : IAgentModelClient
    {
        public TaskCompletionSource? Gate;
        public bool FailNext;
        public List<AgentModelRequest> Requests { get; } = [];
        public async Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (Gate is { } gate) await gate.Task; // Deliberately ignore cancellation to test stale results.
            if (FailNext) { FailNext = false; throw new SafeFailure("connection_failed"); }
            using var doc = JsonDocument.Parse(request.Content.Text!.Split("\nAttached image packets in order: ")[0]);
            var root = doc.RootElement;
            var last = root.GetProperty("observations").EnumerateArray().Last();
            var evidence = new EvidenceRef(last.GetProperty("id").GetGuid(), 0, last.GetProperty("textLength").GetInt32());
            if (request.SystemPrompt.Contains("方案审核者"))
                return Action("review_result", new SolutionReview(true, "Evidence gathering is scoped and executable", [evidence]));
            if (root.GetProperty("toolHistory").GetArrayLength() == 0)
            {
                var previous = root.GetProperty("memory").GetProperty("attempts").EnumerateArray().LastOrDefault();
                var assessments = previous.ValueKind == JsonValueKind.Undefined ? ImmutableArray<AttemptAssessment>.Empty :
                    [new AttemptAssessment(previous.GetProperty("id").GetGuid(), "missing-source", "gather", AttemptOutcome.Claimed, false, false, [evidence], [])];
                return Action("stage_memory_changes", new MemoryChanges([new("contract", "contract", "Need current Solve before editing", [evidence])], assessments));
            }
            return Action("publish_directive", new Directive("读取当前函数", "在平台定位 Solve", "Return current Solve and its test feedback; do not edit yet.",
                "当前函数和完整反馈", "missing-source", "gather", "Obtain current source", [evidence]));
        }
        private static AgentAction Action<T>(string name, T value) => new(name, JsonSerializer.SerializeToElement(value, CodingJson.Options));
    }
    private sealed class Ordinary : IBackendClient
    {
        public int Calls;
        public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, [EnumeratorCancellation] CancellationToken token)
        { Calls++; await Task.Yield(); yield return new("ordinary answer"); yield return new(Completed: true); }
    }
}
