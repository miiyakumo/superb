using System.Windows;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class MainWindow
{
    private readonly WorkspaceDraft ordinaryWorkspace = new();
    private WorkspaceDraft workspace => ordinaryWorkspace;
    internal IReadOnlyList<WorkspaceAttachment> WorkspaceItems => workspace.Items;
    private string? workspaceSceneId = "general";
    private CancellationTokenSource? captureSelectionCancellation;
    private ScenePreset? WorkspaceScene => config.Scenes.FirstOrDefault(s => s.Id == workspaceSceneId);
    private bool Working => preparing || coordinator.State is RequestState.PolicyCheck or RequestState.Connecting or RequestState.Waiting or RequestState.Thinking or RequestState.Streaming;
    private int? DirectRetryLimit => coordinator.Latest?.Code == "response_token_limit" && coordinator.Current is { } prior &&
        (prior.Request.Backend.ThinkingMode != ThinkingMode.Disabled || prior.Request.Backend.MaxTokens < 16384) &&
        DeepSeekSetup.IsOfficialEndpoint(prior.Request.Backend.Endpoint) ? Math.Max(16384, prior.Request.Backend.MaxTokens) : null;
    private void RefreshWorkspace()
    {
        if (reader is null) return;
        var scene = WorkspaceScene;
        var choices = config.Scenes.Prepend(new ScenePreset("", "直接提问", "", new("unused", "Ctrl+Alt+N", Enabled: false)));
        string receiver;
        try
        {
            var target = SceneRequestFactory.SelectBackend(config, scene);
            receiver = $"下次：{target.Name} / {target.Model}";
        }
        catch { receiver = "请先连接或选择模型"; }
        if (coordinator.Current is { } current)
        {
            var actual = $"本次：{current.Request.Backend.Name} / {current.Request.Backend.Model}";
            receiver = Working ? actual : receiver + " · " + actual;
        }
        var key = config.AllHotkeys().FirstOrDefault(k => k.Action == "through");
        var recoveryKey = key is not null && hotkeys?.HasAction("through") == true ? key.Gesture : "穿透快捷键不可用";
        var shownScene = Working && coordinator.Current is { } active ? active.Request.SceneId ?? "" : scene?.Id ?? "";
        reader.Configure(choices, shownScene, receiver, recoveryKey);
        var update = coordinator.Latest;
        var partial = update?.State is RequestState.Cancelled or RequestState.Failed;
        reader.SetActions(coordinator.Mode == RunState.Ready, Working, workspace.HasInput(""),
            !string.IsNullOrEmpty(update?.Text) && !Working,
            partial, coordinator.Current is not null && update?.State is RequestState.Completed or RequestState.Failed or RequestState.Cancelled);
        reader.SetSelecting(captureSelectionCancellation is not null);
        reader.SetDirectRetry(DirectRetryLimit);
        reader.SetShortcutHints(config.AllHotkeys().ToDictionary(binding => binding.Action, binding =>
            ShortcutLabel.Describe(binding, hotkeys?.HasAction(binding.Action) == true,
                hotkeys?.RegistrationErrors.GetValueOrDefault(binding.Action))));
    }
    private void OpenWorkspace(bool hideControl = true, bool explicitHide = false)
    {
        reader.Show(); RefreshWorkspace();
        if (hideControl && reader.IsVisible && CanHideControl())
        {
            // A system dialog owned by settings also counts as foreground. A completed
            // connection must not hide its owner and make Windows select another app.
            if (explicitHide || !ControlOwnsForeground()) Hide();
            else Status("浮窗已打开；设置仍在前台，点击“返回工作区”收起设置（Windows 会转移焦点）");
        }
        else if (hideControl && !CanHideControl()) Status("浮窗已打开；关键快捷键不可用，保留控制中心入口");
    }
    private bool ControlOwnsForeground()
    {
        var foreground = Native.GetForegroundWindow();
        return handle != 0 && (foreground == handle || Native.GetAncestor(foreground, 3) == handle); // GA_ROOTOWNER
    }
    private void SelectWorkspaceScene(string? id)
    {
        if (id == workspaceSceneId) return;
        var previousScene = DiagnosticScene(workspaceSceneId);
        if (Working) StopGeneration();
        workspaceSceneId = id;
        TraceWorkspace(WorkspaceEvent.SceneChanged, previousScene: previousScene);
        reader.SetInputs(workspace.Items);
        if (coordinator.Latest is { } previous) reader.Render(previous.Text, StateLabel(previous));
        else ClearOutput();
        RefreshWorkspace();
    }
    private Task StageClipboard()
    {
        var acquisitionId = Guid.NewGuid();
        TraceWorkspace(WorkspaceEvent.AcquisitionStarted, WorkspaceReason.Clipboard, operationId: acquisitionId);
        if (!Ready()) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.NotReady, operationId: acquisitionId); return Task.CompletedTask; }
        if (Working) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.Busy, operationId: acquisitionId); Status("正在处理；请先停止生成"); return Task.CompletedTask; }
        var version = ++operation;
        preparing = true; RefreshWorkspace(); Status("读取剪贴板");
        try
        {
            var (snapshot, own) = readWorkspaceClipboard();
            if (own) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.OwnClipboardOutput, operationId: acquisitionId); Status("已忽略本应用的剪贴板输出"); return Task.CompletedTask; }
            TraceWorkspace(WorkspaceEvent.AcquisitionRead, WorkspaceReason.Clipboard, acquisitionId, snapshot);
            if (version != operation || !Ready()) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.StaleOperation, acquisitionId, snapshot); return Task.CompletedTask; }
            QueueSnapshot(snapshot, snapshot.Content.HasImage ? "剪贴板图片" : "剪贴板文本", acquisitionId: acquisitionId);
            if (!reader.IsVisible) reader.Show();
            Status($"已加入队列 · {workspace.Items.Count} 项 · 可继续截图或读取剪贴板");
        }
        catch (Exception ex) { TraceWorkspace(WorkspaceEvent.AcquisitionFailed, WorkspaceReason.Clipboard, acquisitionId, errorCode: ConnectionDiagnostics.CodeFor(ex)); if (version == operation) Report(ex); }
        finally
        {
            if (version == operation) { preparing = false; RefreshWorkspace(); }
        }
        return Task.CompletedTask;
    }
    internal void QueueSnapshot(InputSnapshot snapshot, string label, bool startNewTask = false, Guid? acquisitionId = null)
    {
        // A new acquisition after an answer starts the next task, like Quiz; cancellation before
        // acquisition leaves the previous answer and its input intact.
        if (startNewTask || coordinator.Latest is not null)
        {
            // Validate first so a rejected replacement is never reported as a cleared queue.
            var candidate = new WorkspaceDraft(); candidate.Add(snapshot, label);
            TraceWorkspace(WorkspaceEvent.QueueReplacing, startNewTask ? WorkspaceReason.ReplaceForScreenshot : WorkspaceReason.ReplaceAfterAnswer,
                acquisitionId, snapshot);
            workspace.Set(snapshot, label);
        }
        else workspace.Add(snapshot, label);
        TraceWorkspace(WorkspaceEvent.MaterialQueued, operationId: acquisitionId, snapshot: snapshot);
        coordinator.Invalidate(); completed = null; latestUpdate = null; ClearOutput();
        reader.SetInputs(workspace.Items);
        RefreshWorkspace();
    }
    private async Task StageScreenshot(bool region, bool sendImmediately = false)
    {
        var acquisitionId = Guid.NewGuid();
        var source = region ? WorkspaceReason.RegionScreenshot : sendImmediately ? WorkspaceReason.ScreenAndSend : WorkspaceReason.ScreenScreenshot;
        TraceWorkspace(WorkspaceEvent.AcquisitionStarted, source, acquisitionId);
        if (!Ready()) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.NotReady, acquisitionId); return; }
        if (Working) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.Busy, acquisitionId); Status("正在处理；请先停止当前任务"); return; }
        if (!sendImmediately && coordinator.Latest is null && workspace.ImageCount >= Policy.MaximumImages) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.QueueLimit, acquisitionId); Status("最多 5 张图片，请先移除或发送队列"); return; }
        if (captureTask is { IsCompleted: false }) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.CapturePending, acquisitionId); Status("上次采集尚未结束，请稍后重试"); return; }
        // Freeze the recipient and scene before asynchronous capture; settings may change
        // while the windows are hidden. This action never silently changes the receiver.
        var settings = config; var scene = WorkspaceScene;
        PreparedRequest? capturedRequest = null;
        if (sendImmediately)
        {
            try
            {
                var targetBackend = SceneRequestFactory.SelectBackend(settings, scene);
                Policy.ValidateBackend(targetBackend);
                if (!targetBackend.SupportsImages) throw new SafeFailure("backend_has_no_vision");
            }
            catch (Exception ex) { TraceWorkspace(WorkspaceEvent.AcquisitionFailed, source, acquisitionId, errorCode: ConnectionDiagnostics.CodeFor(ex)); throw; }
        }
        var version = ++operation; var wasVisible = reader.IsVisible; var controlVisible = IsVisible;
        var captured = false;
        using var selection = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        preparing = true; RefreshWorkspace();
        try
        {
            var monitors = CaptureService.Monitors();
            CaptureTarget target;
            if (region)
            {
                captureSelectionCancellation = selection;
                if (!reader.IsVisible) reader.Show();
                if (IsVisible) Hide();
                if (!reader.SetClickThrough(true)) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.ClickThroughUnavailable, acquisitionId); return; }
                Status("两点截图：点击第一个角 · 右键或 Esc 取消");
                var corners = await TwoPointSelector.SelectAsync(_ => Dispatcher.BeginInvoke(() =>
                {
                    if (version == operation) Status("两点截图：点击对角 · 右键或 Esc 取消");
                }), selection.Token);
                if (corners is null) { TraceWorkspace(WorkspaceEvent.AcquisitionCancelled, WorkspaceReason.UserCancelled, acquisitionId); Status("截图已取消 · 现有队列保留"); return; }
                if (version != operation || !Ready()) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.StaleOperation, acquisitionId); return; }
                target = CaptureService.FromCorners(corners.Value.First, corners.Value.Second, monitors);
            }
            else
            {
                if (!Native.GetCursorPos(out var point)) throw new SafeFailure("capture_unavailable");
                target = CaptureService.MonitorAt(point, monitors);
            }
            var sequence = clipboard!.Sequence;
            Status("正在采集截图");
            if (IsVisible) Hide();
            reader.Hide();
            if (Native.DwmFlush() != 0) throw new SafeFailure("capture_unavailable");
            var worker = captureScreenshot(target); captureTask = worker;
            var payload = await worker.WaitAsync(TimeSpan.FromSeconds(5));
            if (ReferenceEquals(captureTask, worker)) captureTask = null;
            if (version != operation || !Ready()) { TraceWorkspace(WorkspaceEvent.AcquisitionSkipped, WorkspaceReason.StaleOperation, acquisitionId); return; }
            var snapshot = new InputSnapshot(Guid.NewGuid(), InputKind.Screen, payload, sequence, DateTimeOffset.UtcNow);
            TraceWorkspace(WorkspaceEvent.AcquisitionRead, source, acquisitionId, snapshot);
            var label = region ? "两点截图" : "当前屏幕";
            if (sendImmediately)
            {
                // Validate this screenshot alone without clearing the last answer or
                // changing the user's queue when capture/capability/size checks fail.
                var candidate = new WorkspaceDraft();
                candidate.Add(snapshot, label);
                capturedRequest = SceneRequestFactory.Prepare(settings, scene, candidate.Freeze("", sequence));
            }
            QueueSnapshot(snapshot, label, startNewTask: sendImmediately, acquisitionId: acquisitionId);
            captured = true;
            Status(sendImmediately ? "截图完成 · 正在发送" : $"已加入队列 · {workspace.Items.Count} 项 / {workspace.ImageCount} 张图 · 可继续截图或读取剪贴板");
        }
        catch (OperationCanceledException) { TraceWorkspace(WorkspaceEvent.AcquisitionCancelled, WorkspaceReason.CaptureCancelled, acquisitionId); if (version == operation) Status("截图已取消或超时 · 现有队列保留"); }
        catch (Exception ex) { TraceWorkspace(WorkspaceEvent.AcquisitionFailed, source, acquisitionId, errorCode: ConnectionDiagnostics.CodeFor(ex)); if (version == operation) Report(ex); }
        finally
        {
            if (ReferenceEquals(captureSelectionCancellation, selection)) captureSelectionCancellation = null;
            if (version == operation)
            {
                preparing = false;
                if (coordinator.Mode == RunState.Ready)
                {
                    reader.SetClickThrough(false);
                    if (captured || wasVisible) reader.Show(); else reader.Hide();
                    if (!captured && controlVisible) Show();
                }
                RefreshWorkspace();
            }
            ObserveCaptureWorker();
        }
        if (captured && capturedRequest is not null && version == operation && Ready())
            await SendPrepared(capturedRequest);
    }
    private void ObserveCaptureWorker()
    {
        if (captureTask is not { } outstanding) return;
        _ = outstanding.ContinueWith(finished => Dispatcher.BeginInvoke(() =>
        {
            _ = finished.Exception;
            if (ReferenceEquals(captureTask, outstanding)) captureTask = null;
        }), TaskScheduler.Default);
    }
    private async Task SendWorkspace()
    {
        if (!Ready()) return;
        if (Working) { Status("正在处理；请先停止生成"); return; }
        if (config.Backends.IsDefaultOrEmpty) { Status("尚未连接模型，请打开“设置”并点击设置窗口连接"); return; }
        if (!workspace.HasInput("")) { Status("请先读取剪贴板或收集截图"); return; }
        var version = ++operation;
        var settings = config; var scene = WorkspaceScene;
        preparing = true; RefreshWorkspace();
        try
        {
            var snapshot = workspace.Freeze("", clipboard!.Sequence);
            if (snapshot.Content.Size > Policy.AbsoluteInputLimit) throw new SafeFailure("input_too_large");
            coordinator.Invalidate(); completed = null; latestUpdate = null; ClearOutput();
            if (version != operation || !Ready()) return;
            var request = SceneRequestFactory.Prepare(settings, scene, snapshot);
            reader.SetClickThrough(false);
            if (!reader.IsVisible) reader.Show();
            await SendPrepared(request);
        }
        catch (Exception ex) { if (version == operation) { Report(ex); } }
        finally { if (version == operation) { preparing = false; RefreshWorkspace(); } }
    }
    private async Task SendPrepared(PreparedRequest request)
    {
        if (!Ready()) return;
        completed = null; latestUpdate = null; ClearOutput();
        await coordinator.RunAsync(request);
        RefreshWorkspace();
    }
    private void StopGeneration()
    {
        if (!Working) { Status("当前没有正在生成的任务"); return; }
        captureSelectionCancellation?.Cancel();
        if ((captureSelectionCancellation is not null || captureTask is { IsCompleted: false }) && reader.Check()) reader.Show();
        operation++; preparing = false;
        completed = null; latestUpdate = null;
        coordinator.Stop();
        if (coordinator.Latest is { State: RequestState.Cancelled } stopped && protection.Check() && reader.Check())
        {
            reader.Render(stopped.Text, "已停止 · 部分回答，未完成");
        }
        Status(coordinator.Latest?.State == RequestState.Cancelled ? "已停止 · 已生成内容保留，未自动写回" : "操作已停止 · 现有内容保留"); RefreshWorkspace();
    }
    private void NewWorkspaceTask()
    {
        var clearId = Guid.NewGuid(); var previousTaskId = workspaceTaskId;
        TraceWorkspace(WorkspaceEvent.TaskClearing, WorkspaceReason.NewTask, operationId: clearId);
        captureSelectionCancellation?.Cancel();
        if ((captureSelectionCancellation is not null || captureTask is { IsCompleted: false }) && reader.Check()) reader.Show();
        operation++; preparing = false;
        coordinator.Invalidate(); completed = null; latestUpdate = null;
        workspaceTaskId = Guid.NewGuid();
        workspace.Clear();
        TraceWorkspace(WorkspaceEvent.TaskCleared, WorkspaceReason.NewTask, operationId: clearId, previousTaskId: previousTaskId);
        reader.Clear(); ClearOutput(); Status("等待输入 · 新任务"); RefreshWorkspace();
    }
    private void CopyWorkspaceAnswer()
    {
        if (!Ready() || Working || coordinator.Latest is not { Text.Length: > 0 } answer) return;
        clipboard!.Write(answer.Text, answer.Attempt.Id, answer.Attempt.Generation, null);
        Status(answer.State == RequestState.Completed ? "回答已复制" : "部分回答已复制 · 内容未完成");
    }
    private async Task RetryDirectAnswer()
    {
        if (!Ready() || Working || DirectRetryLimit is not { } limit || coordinator.Current is not { } prior) return;
        // Explicit one-attempt recovery: retain frozen input and show the new budget.
        // It neither changes saved defaults nor silently repeats the original paid request.
        var request = prior.Request with { Backend = prior.Request.Backend with { ThinkingMode = ThinkingMode.Disabled, MaxTokens = limit } };
        reader.SetClickThrough(false);
        await SendPrepared(request);
    }
}
