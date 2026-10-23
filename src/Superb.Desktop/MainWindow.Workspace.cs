using System.Windows;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class MainWindow
{
    private readonly WorkspaceDraft workspace = new();
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
        var key = config.AllHotkeys().FirstOrDefault(k => k.Action == "interact");
        var editKey = key is not null && hotkeys?.HasAction("interact") == true ? key.Gesture : "编辑快捷键不可用";
        var shownScene = Working && coordinator.Current is { } active ? active.Request.SceneId ?? "" : scene?.Id ?? "";
        reader.Configure(choices, shownScene, receiver, editKey);
        var update = coordinator.Latest;
        var partial = update?.State is RequestState.Cancelled or RequestState.Failed;
        reader.SetActions(coordinator.Mode == RunState.Ready, Working, workspace.HasInput(reader.Draft),
            !string.IsNullOrEmpty(update?.Text) && !Working, partial, coordinator.Current is not null && update?.State is RequestState.Completed or RequestState.Failed or RequestState.Cancelled);
        reader.SetSelecting(captureSelectionCancellation is not null);
        reader.SetDirectRetry(DirectRetryLimit);
        reader.SetShortcutHints(config.AllHotkeys().ToDictionary(binding => binding.Action, binding =>
            ShortcutLabel.Describe(binding, config.ExclusiveHotkeysEnabled, hotkeys?.HasAction(binding.Action) == true,
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
            else Status("浮窗已打开；设置仍在前台，点击“返回悬浮工作区”收起设置（Windows 会转移焦点）");
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
        if (Working) { Status("请先停止生成，再切换场景"); return; }
        workspaceSceneId = id;

        RefreshWorkspace();
    }
    private Task StageClipboard()
    {
        if (!Ready()) return Task.CompletedTask;
        if (Working) { Status("正在处理；请先停止生成"); return Task.CompletedTask; }
        var version = ++operation;
        preparing = true; RefreshWorkspace(); Status("读取剪贴板");
        try
        {
            var (snapshot, own) = clipboard!.Read();
            if (own) { Status("已忽略本应用的剪贴板输出"); return Task.CompletedTask; }
            if (version != operation || !Ready()) return Task.CompletedTask;
            QueueSnapshot(snapshot, snapshot.Content.HasImage ? "剪贴板图片" : "剪贴板文本");
            if (!reader.IsVisible) reader.Show();
            Status($"已加入队列 · {workspace.Items.Count} 项 · 可继续截图或读取剪贴板");
        }
        catch (Exception ex) { if (version == operation) Report(ex); }
        finally
        {
            if (version == operation) { preparing = false; RefreshWorkspace(); }
        }
        return Task.CompletedTask;
    }
    internal void QueueSnapshot(InputSnapshot snapshot, string label)
    {
        // A new acquisition after an answer starts the next task, like Quiz; cancellation before
        // acquisition leaves the previous answer and its input intact.
        if (coordinator.Latest is not null) { workspace.Set(snapshot, label); reader.SetDraft(""); }
        else workspace.Add(snapshot, label);
        coordinator.Invalidate(); completed = null; latestUpdate = null; ClearOutput();
        reader.SetInputs(workspace.Items);
    }
    private async Task StageScreenshot(bool region)
    {
        if (!Ready()) return;
        if (Working) { Status("正在处理；请先停止当前任务"); return; }
        if (coordinator.Latest is null && workspace.ImageCount >= Policy.MaximumImages) { Status("最多 5 张图片，请先移除或发送队列"); return; }
        if (captureTask is { IsCompleted: false }) { Status("上次采集尚未结束，请稍后重试"); return; }
        var version = ++operation; var wasVisible = reader.IsVisible; var controlVisible = IsVisible;
        var captured = false;
        using var selection = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        preparing = true;
        try
        {
            var monitors = CaptureService.Monitors();
            CaptureTarget target;
            if (region)
            {
                captureSelectionCancellation = selection;
                if (!reader.IsVisible) reader.Show();
                if (IsVisible) Hide();
                if (!reader.SetMode(false, true)) return;
                Status("两点截图：点击第一个角 · 右键或 Esc 取消");
                var corners = await TwoPointSelector.SelectAsync(_ => Dispatcher.BeginInvoke(() =>
                {
                    if (version == operation) Status("两点截图：点击对角 · 右键或 Esc 取消");
                }), selection.Token);
                if (corners is null) { Status("截图已取消 · 现有队列保留"); return; }
                if (version != operation || !Ready()) return;
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
            var worker = Task.Run(() => CaptureService.Capture(target)); captureTask = worker;
            var payload = await worker.WaitAsync(TimeSpan.FromSeconds(5));
            if (ReferenceEquals(captureTask, worker)) captureTask = null;
            if (version != operation || !Ready()) return;
            QueueSnapshot(new(Guid.NewGuid(), InputKind.Screen, payload, sequence, DateTimeOffset.UtcNow), region ? "两点截图" : "当前屏幕");
            captured = true;
            Status($"已加入队列 · {workspace.Items.Count} 项 / {workspace.ImageCount} 张图 · 可继续截图或读取剪贴板");
        }
        catch (OperationCanceledException) { if (version == operation) Status("截图已取消或超时 · 现有队列保留"); }
        catch (Exception ex) { if (version == operation) Report(ex); }
        finally
        {
            if (ReferenceEquals(captureSelectionCancellation, selection)) captureSelectionCancellation = null;
            if (version == operation)
            {
                preparing = false;
                if (coordinator.Mode == RunState.Ready)
                {
                    reader.SetMode(false);
                    if (captured || wasVisible) reader.Show(); else reader.Hide();
                    if (!captured && controlVisible) Show();
                }
                RefreshWorkspace();
            }
            ObserveCaptureWorker();
        }
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
        if (!workspace.HasInput(reader.Draft)) { Status("请先输入问题或收集输入"); return; }
        var version = ++operation;
        var settings = config; var scene = WorkspaceScene;
        preparing = true; RefreshWorkspace();
        try
        {
            var snapshot = workspace.Freeze(reader.Draft, clipboard!.Sequence);
            if (snapshot.Content.Size > Policy.AbsoluteInputLimit) throw new SafeFailure("input_too_large");
            coordinator.Invalidate(); completed = null; latestUpdate = null; ClearOutput();
            if (version != operation || !Ready()) return;
            var request = SceneRequestFactory.Prepare(settings, scene, snapshot);
            reader.SetMode(false);
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
        if (captureSelectionCancellation is not null && reader.Check()) reader.Show();
        operation++; preparing = false; completed = null; latestUpdate = null;
        coordinator.Stop();
        if (coordinator.Latest is { State: RequestState.Cancelled } stopped && protection.Check() && reader.Check())
        {
            reader.Render(stopped.Text, "已停止 · 部分回答，未完成");
        }
        Status(coordinator.Latest?.State == RequestState.Cancelled ? "已停止 · 已生成内容保留，未自动写回" : "操作已停止 · 现有内容保留"); RefreshWorkspace();
    }
    private void NewWorkspaceTask()
    {
        captureSelectionCancellation?.Cancel();
        if (captureSelectionCancellation is not null && reader.Check()) reader.Show();
        operation++; preparing = false; coordinator.Invalidate();
        completed = null; latestUpdate = null; workspace.Clear();
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
        reader.SetMode(false);
        await SendPrepared(request);
    }
}
