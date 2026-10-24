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
        tests.Add(("No-progress pause exposes checkpoint retry and keeps newly queued feedback separate", () =>
        {
            using var host = new Host(); host.Choose("coding"); host.Model.RepeatRetrieval = true;
            host.Window.QueueSnapshot(Input("material already visible in context"), "source"); Wait(host.Window.RunCommand("send"));
            Check(host.Window.WorkspaceCodingRunner.Latest is { State: CodingRunState.Paused, Calls: 6 });
            Check(host.Button("RetryButton").IsEnabled && !host.Button("StopButton").IsVisible);
            Check(((TextBlock)host.Reader.FindName("CodingProgressState")).Text.Contains("暂停"));
            Check(((TextBlock)host.Reader.FindName("CodingProgressError")).Text.Contains("coding_no_progress"));
            Snapshot(host.Reader, args, "coding-no-progress-checkpoint");
            host.Window.QueueSnapshot(Input("UNSUBMITTED_FEEDBACK_AFTER_PAUSE"), "pending");
            host.Model.RepeatRetrieval = false; Wait(host.Window.RunCommand("retry"));
            Check(host.Window.WorkspaceCodingRunner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 1 });
            Check(host.QueueCount == 1 && host.Window.WorkspaceCodingSession.Observations.Count == 1);
            Check(!RequestText(host.Model.Requests[^1]).Contains("UNSUBMITTED_FEEDBACK_AFTER_PAUSE"));
            AgentModelClient.BuildMessages(host.Model.Requests[^1]);
        }));
        tests.Add(("Coding timeout visibly identifies the transport stage and last receive progress", () =>
        {
            using var host = new Host(); host.Choose("coding");
            foreach (var stage in new[] { CodingTransportStage.WaitingForResponseHeaders, CodingTransportStage.WaitingForResponseBody, CodingTransportStage.ReadingResponseBody })
            {
                var metrics = new CodingTransportTrace().Snapshot with
                {
                    Stage = stage, ElapsedMilliseconds = 300000, StageElapsedMilliseconds = 299000,
                    RequestBytes = 2048, ResponseBytes = stage == CodingTransportStage.ReadingResponseBody ? 128 : 0,
                    MillisecondsSinceLastByte = stage == CodingTransportStage.ReadingResponseBody ? 298000 : null
                };
                var update = new CodingRunUpdate(Guid.NewGuid(), CodingRunState.Failed, 3, "request_timed_out",
                    ElapsedMilliseconds: 301000, ErrorCode: "request_timed_out", Transport: metrics);
                host.Reader.RenderCodingProgress(update);
                var error = (TextBlock)host.Reader.FindName("CodingProgressError");
                var progress = (TextBlock)host.Reader.FindName("CodingProgressMetrics");
                Check(error.Text.Contains("中止位置：" + CodingTransportTrace.StageLabel(stage)) && error.Text.Contains("request_timed_out"));
                Check(progress.Text.Contains("已接收") && (stage != CodingTransportStage.ReadingResponseBody || progress.Text.Contains("距最后收包")));
                foreach (var width in new[] { 600, 360 })
                {
                    host.Reader.Width = width; host.Reader.Height = width == 360 ? 480 : 740; host.Reader.UpdateLayout();
                    Check(error.ActualHeight > 0 && error.TransformToAncestor(host.Reader).Transform(new Point(0, error.ActualHeight)).Y < host.Reader.ActualHeight - 30);
                    Snapshot(host.Reader, args, $"coding-timeout-{stage}-{width}");
                }
            }
        }));
        tests.Add(("Second analysis shows live progress and a persistent failure banner above the retained first directive", () =>
        {
            using var host = new Host(); host.Choose("coding");
            host.Window.QueueSnapshot(Input("initial material"), "source"); Wait(host.Window.RunCommand("send"));
            var firstAnswer = host.Answer;
            Check(((TextBlock)host.Reader.FindName("CodingProgressState")).Text.Contains("分析完成"));
            host.Model.Gate = new(); host.Model.FailNext = true; host.Model.FailureCode = "http_401";
            host.Window.QueueSnapshot(Input("new execution feedback"), "feedback");
            var pending = host.Window.RunCommand("send");
            Check(((Border)host.Reader.FindName("CodingProgressPanel")).IsVisible);
            Check(!((Expander)host.Reader.FindName("ComposerExpander")).IsExpanded);
            var metrics = (TextBlock)host.Reader.FindName("CodingProgressMetrics");
            Check(metrics.Text.Contains("当前请求等待") && metrics.Text.Contains("1 次模型调用"));
            Check(((TextBlock)host.Reader.FindName("CodingActivityText")).Text.Contains("等待模型响应"));
            var initialMetrics = metrics.Text; Wait(Task.Delay(1300));
            Check(metrics.Text != initialMetrics && metrics.Text.Contains("当前请求等待"));
            Snapshot(host.Reader, args, "coding-waiting");
            host.Model.Gate.SetResult(); Wait(pending);
            var state = (TextBlock)host.Reader.FindName("CodingProgressState");
            var error = (TextBlock)host.Reader.FindName("CodingProgressError");
            Check(state.Text.Contains("本次分析失败") && state.Text.Contains("上一轮指令"));
            Check(error.IsVisible && error.Text.Contains("http_401") && error.Text.Contains("认证"));
            Check(host.Answer == firstAnswer && host.Button("RetryButton").IsEnabled);
            Check(((TextBlock)host.Reader.FindName("CodingActivityText")).Text.Contains("分析失败"));
            foreach (var (width, height) in new[] { (600, 740), (360, 480) })
            {
                host.Reader.Width = width; host.Reader.Height = height; host.Reader.UpdateLayout();
                var bottom = error.TransformToAncestor(host.Reader).Transform(new Point(0, error.ActualHeight));
                Check(error.ActualHeight > 0 && bottom.Y < host.Reader.ActualHeight - 30);
                Check(host.Button("RetryButton").IsVisible);
                Snapshot(host.Reader, args, $"coding-failed-{width}");
            }
            Wait(host.Window.RunCommand("newTask"));
            Check(((TextBlock)host.Reader.FindName("CodingActivityText")).Text == "");
        }));
        tests.Add(("Analysis parameters live in program scene settings; edits require saving and do not mutate active tasks", () =>
        {
            using var host = new Host(); host.Choose("coding");
            Check(host.Reader.FindName("CodingProtocol") is null && host.Window.FindName("CodingProtocol") is null && host.Reader.FindName("CodingCalls") is null && host.Reader.FindName("CodingSeconds") is null);
            Check(host.Reader.FindName("CodingControls") is null);
            Check((string)host.Button("SettingsButton").Content == "程序设置" && (string)host.Button("SendButton").Content == "分析任务");
            Check((string)host.Button("ClipboardAnalyzeButton").Content == "读取剪贴板并分析");
            Check((string)host.Button("StopButton").Content == "停止分析");
            host.SetAnalysisOptions(7, 77, save: false);
            var draftSettings = host.Window.BuildSceneSettings();
            Check(draftSettings.CodingAnalysis is { MaximumCalls: 7, MaximumSeconds: 77 });
            // Pending edits in program settings must not change a task that starts before Save.
            host.Model.Gate = new();
            host.Window.QueueSnapshot(Input("first source"), "source"); var pending = host.Window.RunCommand("send");
            Check(CallLimit(host.Model.Requests.Single()) is null);
            ((Button)host.Window.FindName("SaveSceneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(host.Window.WorkspaceCodingRunner.IsRunning);
            host.Model.Gate.SetResult(); Wait(pending);
            Check(host.Model.Requests.All(r => CallLimit(r) is null));
            host.Model.Gate = null;
            var earlier = host.Model.Requests.Count;
            host.Window.QueueSnapshot(Input("new feedback"), "feedback"); Wait(host.Window.RunCommand("send"));
            Check(host.Model.Requests.Skip(earlier).All(r => CallLimit(r) == 7));
            Check(host.Window.BuildSceneSettings().CodingAnalysis.MaximumCalls == 7);
            host.Choose("general");
            Check((string)host.Button("SendButton").Content == "发送" && (string)host.Button("StopButton").Content == "停止生成");
            var scenes = (ComboBox)host.Window.FindName("ScenePicker");
            scenes.SelectedItem = scenes.Items.Cast<ScenePreset>().Single(s => s.Id == "general");
            Check(((FrameworkElement)host.Window.FindName("CodingAnalysisSettings")).Visibility == Visibility.Collapsed);
        }));
        tests.Add(("Invalid analysis parameters cannot replace saved settings or clear retained task material", () =>
        {
            using var host = new Host(); host.Choose("coding");
            host.Window.QueueSnapshot(Input("retained source"), "source"); host.Window.QueueText("pending requirements");
            host.SetAnalysisOptions(0, 300);
            Check(((TextBlock)host.Window.FindName("StatusLabel")).Text.Contains("1–10000"));
            Check(host.QueueCount == 2 && host.Window.QueuedText().Contains("pending requirements"));
            Wait(host.Window.RunCommand("send"));
            Check(host.Model.Requests.All(r => CallLimit(r) is null));
        }));
        tests.Add(("Coding preserves independent material queues, accumulates grounded feedback and rejects duplicate-only reruns", () =>
        {
            using var host = new Host();
            host.Window.QueueSnapshot(Input("ordinary input"), "ordinary"); host.Window.QueueText("ordinary draft");
            host.Choose("coding");
            Check(host.QueueCount == 0 && host.Window.QueuedText() == "");
            host.Window.QueueSnapshot(Input("Task requires Solve; current function is missing"), "task");
            Wait(host.Window.RunCommand("send"));
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 1 && host.Model.Requests.Count == 1);
            Check(host.Ordinary.Calls == 0 && host.QueueCount == 0);
            Check((string)host.Button("CopyButton").Content == "复制执行指令");
            Check(host.Answer.Contains("下一步方向") && host.Answer.Contains("Return current Solve"));
            Check(!host.Button("RetryButton").IsEnabled);
            host.Window.QueueSnapshot(Input("Task requires Solve; current function is missing"), "duplicate");
            Wait(host.Window.RunCommand("send")); Check(host.Model.Requests.Count == 1);
            host.Window.QueueSnapshot(Input("Platform says done but returns no function"), "feedback");
            Wait(host.Window.RunCommand("send"));
            Check(host.Window.WorkspaceCodingSession.Observations.Count == 2);
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 2);
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts[0].Assessment?.Outcome == AttemptOutcome.Claimed);
            host.Window.QueueText("coding draft"); host.Choose("general");
            Check(host.QueueCount == 2 && host.Window.QueuedText().Contains("ordinary draft"));
            Wait(host.Window.RunCommand("send")); Check(host.Ordinary.Calls == 1);
            host.Choose("coding"); Check(host.Window.QueuedText() == "coding draft" && host.Answer.Contains("Return current Solve"));
            Wait(host.Window.RunCommand("newTask"));
            Check(host.Window.WorkspaceCodingSession.Observations.Count == 0 && host.Window.WorkspaceCodingSession.Memory.Attempts.IsEmpty);
            host.Choose("general"); Check(host.QueueCount == 2 && host.Window.WorkspaceCoordinator.State == RequestState.Completed && host.Answer == "ordinary answer");
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
            Check(host.Model.Requests.All(r => !RequestText(r).Contains("later feedback")));
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
            host.SetAnalysisOptions(6);
            Wait(host.Window.RunCommand("retry"));
            Check(host.Model.Requests.All(r => CallLimit(r) is null));
            Check(host.Model.Requests.All(r => !RequestText(r).Contains("new pending feedback")));
            Check(host.QueueCount == 1 && host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 1);
            Check(!host.Window.WorkspaceCodingRunner.CanRetry);
            Wait(host.Window.RunCommand("retry")); Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 1);
        }));
        tests.Add(("Coding screen analysis keeps memory and the recipient and limits frozen before asynchronous capture", () =>
        {
            var captured = new TaskCompletionSource<Payload>();
            using var host = new Host(_ => captured.Task); host.Choose("coding");
            host.Window.QueueSnapshot(Input("original task"), "task"); Wait(host.Window.RunCommand("send"));
            var pending = host.Window.RunCommand("captureScreenSend");
            var field = typeof(MainWindow).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var saved = (Configuration)field.GetValue(host.Window)!;
            var other = saved.Backends[0] with { Id = "other", Model = "other-model" };
            field.SetValue(host.Window, saved with { Backends = saved.Backends.Add(other), SelectedBackendId = other.Id });
            host.SetAnalysisOptions(6);
            captured.SetResult(ImageProcessor.Encode(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
            Wait(pending);
            Check(host.Window.WorkspaceCodingSession.Observations.Count == 2);
            Check(host.Window.WorkspaceCodingSession.Memory.Attempts.Length == 2 && host.Ordinary.Calls == 0);
            Check(host.Model.Requests.All(r => r.Backend.Id == "test" && CallLimit(r) is null));
            Check(((TextBlock)host.Reader.FindName("ReceiverText")).Text.Contains("本次：test / test"));
        }));
        tests.Add(("Failed Coding analysis can use changed options with retained evidence through Analyze", () =>
        {
            using var host = new Host(); host.Choose("coding"); host.Model.FailNext = true;
            host.Window.QueueSnapshot(Input("retained evidence"), "source"); Wait(host.Window.RunCommand("send"));
            Check(host.QueueCount == 0 && host.Button("SendButton").IsEnabled);
            host.SetAnalysisOptions(6);
            Wait(host.Window.RunCommand("send"));
            Check(host.Model.Requests.Skip(1).All(r => CallLimit(r) == 6));
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
            var settingsRenderIndex = Array.IndexOf(args, "--render-dir");
            if (settingsRenderIndex >= 0 && settingsRenderIndex + 1 < args.Length)
            {
                host.SetAnalysisOptions();
                ((TabControl)host.Window.FindName("MainTabs")).SelectedItem = host.Window.FindName("ScenesTab");
                host.Window.Show();
                foreach (var (width, height) in new[] { (900, 820), (800, 680) })
                {
                    host.Window.Width = width; host.Window.Height = height; host.Window.UpdateLayout();
                    ((ScrollViewer)host.Window.FindName("SceneEditorScroll")).ScrollToBottom(); host.Window.UpdateLayout();
                    var save = (Button)host.Window.FindName("SaveSceneButton");
                    var position = save.TransformToAncestor(host.Window).Transform(new Point());
                    Check(save.IsVisible && position.Y + save.ActualHeight <= host.Window.ActualHeight);
                    var bitmap = new RenderTargetBitmap((int)host.Window.ActualWidth, (int)host.Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host.Window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(args[settingsRenderIndex + 1], $"analysis-settings-{width}.png")); encoder.Save(stream);
                }
            }
        }));
    }
    private static string StateText(AgentModelRequest request) => request.Messages.Last(m => m.Role == AgentRole.User && m.Content.Text?.StartsWith("{\"sessionId\"") == true).Content.Text!;
    private static string RequestText(AgentModelRequest request) => string.Join("\n", request.Messages.Select(m => m.Content.Text));
    private static int? CallLimit(AgentModelRequest request)
    {
        using var document = JsonDocument.Parse(StateText(request));
        var limit = document.RootElement.GetProperty("callLimit");
        return limit.ValueKind == JsonValueKind.Null ? null : limit.GetInt32();
    }
    private static void Snapshot(ReaderWindow reader, string[] args, string name)
    {
        var index = Array.IndexOf(args, "--render-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        Directory.CreateDirectory(args[index + 1]); reader.UpdateLayout();
        var surface = (FrameworkElement)reader.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(args[index + 1], name + ".png")); encoder.Save(output);
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
        public void SetAnalysisOptions(int? calls = null, int seconds = 300, bool save = true)
        {
            var scenes = (ComboBox)Window.FindName("ScenePicker");
            scenes.SelectedItem = scenes.Items.Cast<ScenePreset>().Single(s => s.Id == "coding");
            ((TextBox)Window.FindName("CodingCalls")).Text = calls?.ToString() ?? "";
            ((TextBox)Window.FindName("CodingSeconds")).Text = seconds.ToString();
            if (save) ((Button)Window.FindName("SaveSceneButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
        public Button Button(string name) => (Button)Reader.FindName(name);
        public void Choose(string id) => ((ComboBox)Reader.FindName("WorkspaceScene")).SelectedValue = id;
        public void Dispose() { Window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
    }
    private sealed class Model : IAgentModelClient
    {
        public TaskCompletionSource? Gate;
        public bool FailNext;
        public bool RepeatRetrieval;
        public string FailureCode = "connection_failed";
        public List<AgentModelRequest> Requests { get; } = [];
        public async Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (Gate is { } gate) await gate.Task; // Deliberately ignore cancellation to test stale results.
            if (FailNext) { FailNext = false; throw new SafeFailure(FailureCode); }
            using var doc = JsonDocument.Parse(StateText(request));
            var root = doc.RootElement;
            var last = root.GetProperty("observations").EnumerateArray().Last();
            var evidence = new EvidenceRef(last.GetProperty("id").GetGuid(), 0, last.GetProperty("textLength").GetInt32());
            if (RepeatRetrieval) return Action("retrieve_material", new MaterialQuery(Ranges: [evidence]));
            var previous = root.GetProperty("memory").GetProperty("attempts").EnumerateArray().LastOrDefault();
            var assessments = previous.ValueKind == JsonValueKind.Undefined ? ImmutableArray<AttemptAssessment>.Empty :
                [new AttemptAssessment(previous.GetProperty("id").GetGuid(), "missing-source", "gather", AttemptOutcome.Claimed, false, false, [evidence], [])];
            var directive = new Directive("读取当前函数", "在平台定位 Solve", "Return current Solve and its test feedback; do not edit yet.",
                "当前函数和完整反馈", "missing-source", "gather", "Obtain current source", [evidence]);
            return Action("submit_result", new CodingSubmission(directive,
                new([new("contract", "contract", "Need current Solve before editing", [evidence])], assessments)));
        }
        private static AgentAction Action<T>(string name, T value) => new(Guid.NewGuid().ToString("N"), name, CodingJson.Serialize(value));
    }
    private sealed class Ordinary : IBackendClient
    {
        public int Calls;
        public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, [EnumeratorCancellation] CancellationToken token)
        { Calls++; await Task.Yield(); yield return new("ordinary answer"); yield return new(Completed: true); }
    }
}
