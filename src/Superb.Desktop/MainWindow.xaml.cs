using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

public partial class MainWindow : Window
{
    private Configuration config = new();
    private readonly CredentialStore credentials = new();
    private readonly ChatCompletionsClient backend;
    private readonly Coordinator coordinator;
    private readonly ReaderWindow reader = new();
    private readonly Protection protection;
    private readonly SyncFilter syncFilter = new();
    private readonly DispatcherTimer clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer guardTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer layoutSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ClipboardService? clipboard;
    private HotkeyManager? hotkeys;
    private SyncGrant? grant;
    private PreparedRequest? prepared;
    private RequestUpdate? latestUpdate;
    private RequestUpdate? completed;
    private Task<Payload>? captureTask;
    private CancellationTokenSource? probeCancellation;
    private long operation, syncGeneration;
    private bool preparing, exiting, loading, pausedForProtection, sessionLocked, configLoadFailed;
    private bool clipboardListenerReady, sessionListenerReady;
    private DateTimeOffset lastAutoCapture = DateTimeOffset.MinValue;
    private nint handle;
    private readonly bool isolatedWorkspaceTest;
    internal ReaderWindow WorkspaceWindow => reader;
    internal Coordinator WorkspaceCoordinator => coordinator;

    public MainWindow() : this(null, null) { }
    // Isolated synthetic integration host: no user configuration, global hotkeys, clipboard reads or persistence.
    internal MainWindow(Configuration? testConfiguration, IBackendClient? testBackend)
    {
        isolatedWorkspaceTest = testConfiguration is not null && testBackend is not null;
        if (testConfiguration is not null) config = testConfiguration;
        InitializeComponent();
        backend = new(credentials);
        coordinator = new(testBackend ?? backend);
        protection = new(this, false);
        protection.AllowMouseActivation = () => !IsVisible || PresentationSource.FromVisual(this)?.CompositionTarget is null ||
            (Native.GetCursorPos(out var point) &&
                CanActivateSettingsTarget(InputHitTest(PointFromScreen(new Point(point.X, point.Y))) as DependencyObject));
        coordinator.Updated += OnUpdate;
        reader.Command += action => _ = RunCommand(action);
        reader.LayoutChanged += () =>
        {
            config = config with { Panel = reader.Layout() };
            if (!isolatedWorkspaceTest && !configLoadFailed && !exiting) { layoutSaveTimer.Stop(); layoutSaveTimer.Start(); }
        };
        layoutSaveTimer.Tick += (_, _) =>
        {
            layoutSaveTimer.Stop();
            try { config.Save(); } catch { Status("外观已调整，但设置保存失败；本次会话仍可使用"); }
        };
        reader.DraftChanged += () => { if (grant is not null) StopSync(); prepared = null; RefreshWorkspace(); };
        reader.ProtectionLost += () => ProtectionFailed();
        SourceInitialized += InitializePlatform;
        clipboardTimer.Tick += async (_, _) =>
        {
            clipboardTimer.Stop();
            if (grant is null || DateTimeOffset.UtcNow >= grant.ExpiresAt) { StopSync(); return; }
            if (preparing || coordinator.State is RequestState.Connecting or RequestState.Waiting or RequestState.Thinking or RequestState.Streaming or RequestState.PolicyCheck ||
                DateTimeOffset.UtcNow - lastAutoCapture < TimeSpan.FromSeconds(2)) { clipboardTimer.Start(); return; }
            await CaptureInput(false, true);
        };
        guardTimer.Tick += (_, _) =>
        {
            if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
            if (grant is not null && DateTimeOffset.UtcNow >= grant.ExpiresAt) { StopSync(); Status("同步授权已到期"); }
            if (latestUpdate is { } update)
            {
                latestUpdate = null;
                if (coordinator.IsCurrent(update.Attempt))
                {
                    reader.Render(update.Text, StateLabel(update));
                }
            }
        };
        Closing += (_, _) => Shutdown();
        Populate();
    }
    internal bool CanActivateSettingsTarget(DependencyObject? target)
    {
        // Emergency actions do not need an input focus, even in the settings window.
        for (var node = target; node is not null && node != this; node =
            (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, SettingsPauseButton) || ReferenceEquals(node, ResumeButton)) return false;
        return true;
    }
    public new void Show()
    {
        new WindowInteropHelper(this).EnsureHandle();
        // WPF may reset affinity while showing a normal window. Restore only at our owned
        // visibility transitions; the periodic guard still treats external changes as failure.
        if (!protection.RefreshDisplayAffinity()) ProtectionFailed();
        base.Show();
        // Restoring with WindowState.Normal can activate the window. Restore visibly
        // without taking foreground, just as on the first show.
        if (WindowState == WindowState.Minimized) Native.ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
        if (!protection.RefreshDisplayAffinity()) ProtectionFailed();
    }
    public new void Hide()
    {
        base.Hide();
        if (handle != 0 && !protection.RefreshDisplayAffinity()) ProtectionFailed();
    }
    private void InitializePlatform(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(this).Handle;
        var protectedOk = protection.Install();
        clipboard = new(handle);
        if (isolatedWorkspaceTest)
        {
            reader.ApplyLayout(config.Panel);
            new WindowInteropHelper(reader).EnsureHandle();
            coordinator.SetMode(protectedOk && reader.Check() ? RunState.Ready : RunState.ProtectionUnavailable);
            Populate(); UpdateMode(); return;
        }
        hotkeys = new(handle);
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        clipboardListenerReady = Native.AddClipboardFormatListener(handle);
        sessionListenerReady = Native.WTSRegisterSessionNotification(handle, 0);
        try { config = Configuration.Load(); }
        catch { configLoadFailed = true; Status("配置无法读取；已使用安全默认值，未覆盖原文件"); }
        Populate();
        if (config.Backends.IsDefaultOrEmpty) MainTabs.SelectedItem = ConnectionTab;
        reader.ApplyLayout(config.Panel);
        // Source initialization installs protection before any sensitive payload can be rendered.
        new WindowInteropHelper(reader).EnsureHandle();
        RegisterHotkeys();
        var readerOk = reader.Check();
        ProtectionLabel.Text = protectedOk && readerOk
            ? "属性已设置 · 当前配置未实测；尚无对外声明兼容的采集链路。"
            : "未验证 / 不支持 · 敏感显示与发送已阻止。";
        if (!protectedOk || !readerOk) ProtectionFailed();
        else if (!sessionListenerReady) Status("系统会话通知注册失败；保持暂停");
        else
        {
            pausedForProtection = false;
            coordinator.SetMode(RunState.Ready);
            UpdateMode();
            if (!configLoadFailed)
                Status(config.Backends.IsDefaultOrEmpty
                    ? "填入 DeepSeek 密钥，即可开始提问"
                    : "已就绪 · 可使用场景入口或已启用的快捷键");
        }
        guardTimer.Start();
        if (!config.Backends.IsDefaultOrEmpty && protectedOk && readerOk && sessionListenerReady)
            Dispatcher.BeginInvoke(() => OpenWorkspace());
    }
    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x312) { handled = true; if (hotkeys?.GetAction((int)wParam) is { } action) _ = RunCommand(action); }
        if (msg == 0x31D && grant is not null) { clipboardTimer.Stop(); clipboardTimer.Start(); }
        if (msg == 0x2B1)
        {
            if ((int)wParam is 7 or 6 or 4 or 2) // lock, logoff, remote/console disconnect
            {
                sessionLocked = true; EmergencyPause("系统会话已锁定或断开", RunState.Locked);
            }
            else if ((int)wParam is 8 or 5 or 3 or 1)
            {
                sessionLocked = false; EmergencyPause("会话已返回；需要手动恢复");
            }
        }
        if (msg == 0x218 && (int)wParam == 4) EmergencyPause("系统挂起；需要手动恢复");
        if (msg == 0x7E) EmergencyPause("显示配置变化；请重新确认采集目标");
        if (msg == 0x31E && !protection.Check()) ProtectionFailed();
        return 0;
    }
    private void Populate()
    {
        loading = true;
        BackendPicker.ItemsSource = config.Backends;
        BackendPicker.SelectedItem = config.Backends.FirstOrDefault(x => x.Id == config.SelectedBackendId);
        EditBackendPicker.ItemsSource = config.Backends;
        EditBackendPicker.SelectedItem = BackendPicker.SelectedItem;
        PipelineBox.Text = JsonSerializer.Serialize(config.Pipeline.Steps, Configuration.Json);
        TextRoutePicker.ItemsSource = new[] { new BackendChoice("", "跟随默认连接") }
            .Concat(config.Backends.Select(b => new BackendChoice(b.Id, b.Name))).ToArray();
        ImageRoutePicker.ItemsSource = new[] { new BackendChoice("", "跟随默认连接") }
            .Concat(config.Backends.Select(b => new BackendChoice(b.Id, b.SupportsImages ? b.Name : b.Name + "（不支持图像）"))).ToArray();
        TextRoutePicker.SelectedValue = config.Routing.TextBackendId ?? "";
        ImageRoutePicker.SelectedValue = config.Routing.ImageBackendId ?? "";
        PopulateScenesAndKeys();
        ExclusiveReaderKeys.IsChecked = config.ExclusiveHotkeysEnabled;
        AutoCopy.IsChecked = config.AutoCopy;
        ClearClipboard.IsChecked = config.ClearOwnedClipboardOnPause;
        SyncDuration.Text = config.SyncMinutes.ToString();
        DefaultConnection.Visibility = config.Backends.IsDefaultOrEmpty ? Visibility.Collapsed : Visibility.Visible;
        QuickSetup.IsExpanded = config.Backends.IsDefaultOrEmpty;
        BackendSettings.IsExpanded = !config.Backends.IsDefaultOrEmpty;
        TargetLabel.Text = config.Capture?.Label ?? "未配置采集目标";
        var selected = BackendPicker.SelectedItem as Backend;
        ModelSummary.Text = selected is null ? "先连接一个模型，即可开始提问。" : $"{selected.Name} · {selected.Model}";
        if (config.Routing.TextBackendId is not null || config.Routing.ImageBackendId is not null)
        {
            string RouteName(string? id) => config.Backends.FirstOrDefault(x => x.Id == (id ?? config.SelectedBackendId))?.Name ?? "未选择";
            ModelSummary.Text += $" · 固定路由：文字 → {RouteName(config.Routing.TextBackendId)}，图像 → {RouteName(config.Routing.ImageBackendId)}";
        }
        var deepSeek = config.Backends.FirstOrDefault(x => x.Endpoint == DeepSeekSetup.Endpoint);
        QuickStatus.Text = deepSeek is null ? "不需要填写接口地址或模型名称。" : $"已保存 {deepSeek.Name} / {deepSeek.Model}。可直接开始提问，或填入新密钥重新配置。";
        loading = false;
        LoadBackendEditor();
        RefreshWorkspace();
    }
    private void Status(string message) { StatusLabel.Text = message; reader.Status(message); SetError(null); RefreshWorkspace(); }
    private void SetError(string? code)
    {
        ErrorCode.Text = code ?? "";
        ErrorDetails.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        if (code is null) ErrorDetails.IsExpanded = false;
    }
    private void ClearOutput()
    {
        reader.ClearAnswer();
    }
    private static string StateLabel(RequestUpdate update) => (update.State switch
    {
        RequestState.PolicyCheck => "正在校验外发策略", RequestState.Connecting => "连接中", RequestState.Waiting => "等待响应",
        RequestState.Thinking => "模型正在思考 · 尚未返回回答，可停止",
        RequestState.Streaming => "生成中", RequestState.Completed => "已完成", RequestState.Cancelled => "已停止 · 部分回答，未完成",
        RequestState.Failed => ConnectionDiagnostics.Explain(update.Code), _ => update.State.ToString()
    }) + (update.Attempt.Request.ProcessingNotices.IsDefaultOrEmpty ? "" : $" · 已按规则跳过 {update.Attempt.Request.ProcessingNotices.Length} 个可选步骤");
    private void OnUpdate(RequestUpdate update)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnUpdate(update)); return; }
        if (!coordinator.IsCurrent(update.Attempt)) return;
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
        StatusLabel.Text = StateLabel(update);
        SetError(update.Code);
        latestUpdate = update;
        if (update.State is RequestState.Connecting or RequestState.Thinking or RequestState.Streaming) reader.BeginAnswer();
        RefreshWorkspace();
        if (update.State is RequestState.Failed or RequestState.Cancelled or RequestState.Completed)
        {
            reader.Render(update.Text, StateLabel(update));
            latestUpdate = null;
        }
        if (update.State != RequestState.Completed) return;
        completed = update;
        if (!config.AutoCopy || !reader.Check()) return;
        try
        {
            var written = clipboard!.Write(update.Text, update.Attempt.Id, update.Attempt.Generation, update.Attempt.Request.Snapshot.ClipboardSequence);
            if (!written) { Status("已完成；剪贴板已变化，跳过自动写回"); reader.Render(update.Text, "已完成 · 自动写回已跳过"); latestUpdate = null; }
        }
        catch (Exception ex) { Report(ex); }
    }
    private void StopSync()
    {
        // Revoke the live input lease without conflating it with request cancellation.
        // A preparation that has not reached transport observes syncGeneration and is dropped;
        // a request already submitted remains a normal request and can be cancelled separately.
        syncGeneration++;
        grant = null;
        clipboardTimer.Stop();
        syncFilter.Reset();
        SyncSummary.Text = "当前无授权";
        UpdateMode();
    }
    private void UpdateMode()
    {
        ModeLabel.Text = $"{coordinator.Mode switch { RunState.Ready => "就绪", RunState.Locked => "锁定", RunState.ProtectionUnavailable => "保护不可用", _ => "已暂停" }}{(grant is null ? "" : " · 剪贴板同步中")}";
        ResumeButton.Visibility = coordinator.Mode == RunState.Ready ? Visibility.Collapsed : Visibility.Visible;
        RefreshWorkspace();
    }
    public void EmergencyPause(string reason, RunState mode = RunState.Paused)
    {
        captureSelectionCancellation?.Cancel();
        CancelProbe();
        operation++; preparing = false; coordinator.SetMode(mode); prepared = null; completed = null; latestUpdate = null;
        workspace.Clear(); reader.Clear(); ClearOutput(); StopSync(); ApiKey.Clear(); QuickApiKey.Clear();
        if (reader.Check()) reader.SetMode(false);
        if (config.ClearOwnedClipboardOnPause) { try { clipboard?.ClearOwned(); } catch { /* Clear best effort only our own data. */ } }
        UpdateMode(); Status(reason);
    }
    private void ProtectionFailed()
    {
        if (pausedForProtection) return;
        pausedForProtection = true;
        EmergencyPause("保护不可用；敏感内容已遮蔽", RunState.ProtectionUnavailable);
        ProtectionLabel.Text = "未验证 / 不支持 · 敏感显示已关闭";
    }
    private bool Ready()
    {
        if (coordinator.Mode != RunState.Ready) { Status("请先在控制中心恢复会话"); return false; }
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return false; }
        return true;
    }
    private async Task CaptureInput(bool screen, bool automatic = false, ScenePreset? scene = null)
    {
        if (!Ready()) return;
        if (Working) { if (!automatic) Status("正在处理；请先停止生成"); return; }
        if (!automatic && config.Backends.IsDefaultOrEmpty) { MainTabs.SelectedItem = ConnectionTab; Status("请先连接一个模型"); return; }
        if (screen && config.Capture is null)
        {
            OpenCaptureSettings(); return;
        }
        if (!automatic && !IsVisible && !reader.IsVisible) reader.Show();
        var activeGrant = grant;
        var activeSyncGeneration = syncGeneration;
        if (automatic && (activeGrant is null || activeGrant.ExpiresAt <= DateTimeOffset.UtcNow)) { StopSync(); return; }
        var version = ++operation;
        preparing = true;
        RefreshWorkspace();
        try
        {
            var settings = config; // Freeze processing and routing before the asynchronous capture.
            InputSnapshot snapshot;
            if (screen)
            {
                if (captureTask is { IsCompleted: false }) throw new SafeFailure("capture_worker_busy");
                var target = settings.Capture ?? throw new SafeFailure("capture_target_missing");
                var sequence = clipboard!.Sequence;
                Status("采集中");
                var worker = Task.Run(() => CaptureService.Capture(target)); captureTask = worker;
                snapshot = new(Guid.NewGuid(), InputKind.Screen, await worker.WaitAsync(TimeSpan.FromSeconds(5)), sequence, DateTimeOffset.UtcNow);
                if (ReferenceEquals(captureTask, worker)) captureTask = null;
            }
            else
            {
                if (automatic) lastAutoCapture = DateTimeOffset.UtcNow;
                var (captured, own) = clipboard!.Read();
                if (own) { Status("已忽略本应用的剪贴板输出"); return; }
                snapshot = captured;
                if (automatic && (!activeGrant!.Allows(snapshot.Kind, settings.SelectedBackendId, DateTimeOffset.UtcNow, false) ||
                    !syncFilter.Accept(snapshot, own))) return;
            }
            if (version != operation || !Ready()) return;
            reader.SetDraft("");
            workspace.Set(snapshot, screen ? "预设截图范围" : "剪贴板");
            reader.SetInputs(workspace.Items);
            coordinator.Invalidate(); completed = null; prepared = null; latestUpdate = null; ClearOutput();
            Status("本地处理中");
            PreparedRequest request;
            if (scene is not null)
                request = await Task.Run(() => SceneRequestFactory.Prepare(settings, scene, snapshot, new ImageProcessor()));
            else
            {
                var localNotices = new List<string>();
                var content = await Task.Run(() => Preprocessor.Process(snapshot.Content, settings.Pipeline, new ImageProcessor(), localNotices.Add));
                var selected = settings.Routing.Select(content, settings.SelectedBackendId, settings.Backends);
                request = new(snapshot with { Content = content }, content, settings.Pipeline, selected, localNotices.ToImmutableArray());
            }
            if (version != operation || !Ready()) return;
            if (automatic && (activeSyncGeneration != syncGeneration || grant is null)) return;
            var route = request.Backend; var processed = request.Content;
            var notices = request.ProcessingNotices;
            if (automatic && !activeGrant!.Allows(snapshot.Kind, route.Id, DateTimeOffset.UtcNow, false)) throw new SafeFailure("sync_route_not_authorized");
            prepared = request;
            Policy.Check(prepared);
            reader.SetInputs(workspace.Items);
            StatusLabel.Text = $"已准备 · {route.Name} / {route.Model} · 等待发送" + (notices.Length > 0 ? $" · {notices.Length} 个可选步骤已按规则跳过" : "");
            if (automatic) lastAutoCapture = DateTimeOffset.UtcNow;
            var send = automatic ? activeGrant!.AllowSend : true;
            if (send) await SendPrepared(automatic, activeGrant);
        }
        catch (Exception ex) { if (version == operation) { prepared = null; Report(ex); } }
        finally
        {
            if (version == operation) { preparing = false; RefreshWorkspace(); }
            if (captureTask is { } outstanding)
            {
                // PrintWindow cannot be forcibly cancelled. Keep at most one worker, then drop its result.
                _ = outstanding.ContinueWith(finishedTask => Dispatcher.BeginInvoke(() =>
                {
                    _ = finishedTask.Exception;
                    if (ReferenceEquals(captureTask, outstanding)) captureTask = null;
                }), TaskScheduler.Default);
            }
        }
    }
    private async Task SendPrepared(bool automatic = false, SyncGrant? authorization = null)
    {
        if (!Ready()) return;
        if (prepared is null) { Status("尚无已准备输入"); return; }
        var request = prepared;
        prepared = null; completed = null; latestUpdate = null; ClearOutput();
        RefreshWorkspace();
        await coordinator.RunAsync(request, automatic, authorization);
        RefreshWorkspace();
    }
    internal async Task RunCommand(string action)
    {
        try
        {
            if (action.StartsWith("scene:", StringComparison.Ordinal))
            {
                var scene = config.Scenes.FirstOrDefault(s => s.Action == action) ?? throw new SafeFailure("scene_missing");
                await RunScene(scene); return;
            }
            if (action.StartsWith("removeInput:", StringComparison.Ordinal) && Guid.TryParse(action[12..], out var itemId))
            {
                if (!Working) { workspace.Remove(itemId); prepared = null; reader.SetInputs(workspace.Items); Status("已从队列移除"); }
                return;
            }
            switch (action)
            {
                case "workspaceSend": await SendWorkspace(); break;
                case "stageClipboard": await StageClipboard(); break;
                case "stageCapture": await StageScreenshot(true); break;
                case "captureScreen": await StageScreenshot(false); break;
                case "deleteCapture":
                    if (!Working && workspace.Items.LastOrDefault() is { } last) { workspace.Remove(last.Snapshot.Id); prepared = null; reader.SetInputs(workspace.Items); Status("已移除队列最后一项"); }
                    break;
                case "selectScene": SelectWorkspaceScene(reader.SelectedSceneId); break;
                case "removeInput":
                    if (!Working) { workspace.Clear(); prepared = null; reader.ClearInput(); Status("已移除输入"); RefreshWorkspace(); }
                    break;
                case "workspaceCopy": CopyWorkspaceAnswer(); break;
                case "newTask": NewWorkspaceTask(); break;
                case "interact":
                    if (Ready()) { if (!reader.IsVisible) reader.Show(); reader.SetMode(!reader.Interactive); }
                    break;
                case "beginEdit":
                    if (Ready()) reader.SetMode(true);
                    break;
                case "through":
                    if (Ready() && hotkeys?.HasAction("interact") == true && CanHideControl()) reader.SetMode(false, true);
                    else Status("穿透未开启：请先修复编辑、设置、暂停和退出快捷键");
                    break;
                case "ask": await SendWorkspace(); break;
                case "resume": Resume_Click(this, new RoutedEventArgs()); break;
                case "clipboard": await StageClipboard(); break;
                case "capture": await StageScreenshot(true); break;
                case "send": await SendWorkspace(); break;
                case "retryDirect": await RetryDirectAnswer(); break;
                case "retry":
                    if (Ready() && !Working && coordinator.Current is { } prior && coordinator.State is RequestState.Failed or RequestState.Completed or RequestState.Cancelled)
                    {
                        prepared = prior.Request;
                        await SendPrepared(); // Explicit retry; new attempt ID, frozen target and processed body.
                    }
                    else Status("没有可重试的已结束请求");
                    break;
                case "toggle":
                    if (reader.IsVisible)
                    {
                        if (!IsVisible && !CanHideControl()) { Status("关键快捷键不可用；请先打开控制中心"); break; }
                        reader.Hide();
                    }
                    else { reader.Show(); reader.Check(); }
                    break;
                case "copy":
                    if (Ready() && completed is { } done && coordinator.IsCurrent(done.Attempt))
                    { clipboard!.Write(done.Text, done.Attempt.Id, done.Attempt.Generation, null); Status("完整回答已复制"); }
                    else Status("当前没有可复制的完整回答");
                    break;
                case "cancel":
                    StopGeneration(); break;
                case "pause": EmergencyPause("紧急暂停已生效"); break;
                case "pageDown": reader.Page(true); break;
                case "pageUp": reader.Page(false); break;
                case "settings": Show(); break;
                case "exit": Close(); break;
            }
        }
        catch (Exception ex) { Report(ex); }
    }
    private void Report(Exception ex)
    {
        var code = ex is TimeoutException ? "capture_timed_out" : ConnectionDiagnostics.CodeFor(ex);
        Status(ConnectionDiagnostics.Explain(code)); SetError(code);
    }
    private void Mutate(Action action)
    {
        try
        {
            CancelProbe();
            if (configLoadFailed)
            {
                File.Copy(Configuration.FilePath, Configuration.FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false);
                configLoadFailed = false;
            }
            action(); config.Save(); Populate(); Status("配置已保存");
        }
        catch (Exception ex) { Report(ex); }
    }
    private void RegisterHotkeys()
    {
        var failed = hotkeys!.Register(config.AllHotkeys(), config.ExclusiveHotkeysEnabled);
        if (reader.ClickThrough && !hotkeys.HasAction("interact")) reader.SetMode(false);
        HotkeyStatus.Text = failed.Count > 0 ? "未启用的动作：" + string.Join(", ", failed)
            : config.ExclusiveHotkeysEnabled ? "独占接管已开启；已启用的绑定注册成功" : "独占接管关闭；普通组合键可能产生网页按键事件";
        if (failed.Count > 0) Status("快捷键冲突或无效（动作）：" + string.Join(", ", failed));
        RefreshWorkspace();
    }
    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (sessionLocked || !sessionListenerReady) { Status("系统会话尚未就绪"); return; }
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
        pausedForProtection = false; coordinator.SetMode(RunState.Ready); UpdateMode(); Status("已恢复");
    }
    private async Task AskQuestion(ScenePreset? scene = null)
    {
        if (!Ready()) return;
        if (Working) { Status("正在处理；请先停止生成"); return; }
        if (config.Backends.IsDefaultOrEmpty) { MainTabs.SelectedItem = ConnectionTab; Status("请先连接一个模型"); return; }
        var draft = reader.Draft;
        if (string.IsNullOrWhiteSpace(draft)) { Status("请先在浮窗输入问题"); return; }
        var version = ++operation;
        preparing = true;
        try
        {
            var settings = config;
            workspace.Clear(); reader.ClearInput(); RefreshWorkspace();
            var content = Payload.FromText(draft);
            var sequence = clipboard!.Sequence;
            if (content.Size > Policy.AbsoluteInputLimit) throw new SafeFailure("input_too_large");
            coordinator.Invalidate(); prepared = null; completed = null; latestUpdate = null; ClearOutput();
            var snapshot = new InputSnapshot(Guid.NewGuid(), InputKind.ManualText, content, sequence, DateTimeOffset.UtcNow);
            PreparedRequest request;
            if (scene is not null)
                request = await Task.Run(() => SceneRequestFactory.Prepare(settings, scene, snapshot, new ImageProcessor()));
            else
            {
                var notices = new List<string>();
                var processed = await Task.Run(() => Preprocessor.Process(content, settings.Pipeline, new ImageProcessor(), notices.Add));
                var selected = settings.Routing.Select(processed, settings.SelectedBackendId, settings.Backends);
                request = new(snapshot with { Content = processed }, processed, settings.Pipeline, selected, notices.ToImmutableArray());
            }
            if (version != operation || !Ready()) return;
            if (!IsVisible && !reader.IsVisible) reader.Show();
            prepared = request;
            Policy.Check(prepared);
            await SendPrepared();
        }
        catch (Exception ex) { if (version == operation) { prepared = null; Report(ex); } }
        finally { if (version == operation) { preparing = false; RefreshWorkspace(); } }
    }

    private sealed class TemporaryCredential(string secret) : ICredentialStore, IDisposable
    {
        private string value = secret;
        public string Read(string reference) => value;
        public void Dispose() => value = "";
    }
    private void SetProbeBusy(bool busy, bool quick = false)
    {
        TestBackendButton.IsEnabled = !busy;
        QuickConnectButton.IsEnabled = !busy;
        QuickApiKey.IsEnabled = !busy;
        QuickProxyMode.IsEnabled = !busy;
        QuickProxyAddress.IsEnabled = !busy;
        CancelQuickButton.Visibility = busy && quick ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        var secret = QuickApiKey.Password.Trim();
        QuickApiKey.Clear();
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8192 || secret.Any(char.IsControl))
        { QuickStatus.Text = "请填写有效的 API 密钥。"; return; }
        CancelProbe();
        using var cancellation = new CancellationTokenSource();
        probeCancellation = cancellation;
        SetProbeBusy(true, quick: true);
        QuickDetails.Visibility = Visibility.Collapsed;
        QuickStatus.Text = "正在获取模型并测试连接…";
        var existing = config.Backends.FirstOrDefault(x => x.Id == config.SelectedBackendId && x.Endpoint == DeepSeekSetup.Endpoint) ??
            config.Backends.FirstOrDefault(x => x.Endpoint == DeepSeekSetup.Endpoint);
        var draft = DeepSeekSetup.Preset(existing?.Id ?? Guid.NewGuid().ToString("N"), "temporary",
            (ProxyMode)QuickProxyMode.SelectedIndex, QuickProxyAddress.Text.Trim());
        try
        {
            using var temporary = new TemporaryCredential(secret);
            using var client = new ChatCompletionsClient(temporary);
            var result = await DeepSeekSetup.ConnectAsync(client, client, draft, cancellation.Token);
            if (!ReferenceEquals(probeCancellation, cancellation) || !Ready()) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (configLoadFailed)
            {
                File.Copy(Configuration.FilePath, Configuration.FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false);
                configLoadFailed = false;
            }
            config = QuickSetupPersistence.Commit(config, result.Backend, secret, credentials, next => next.Save());
            StopSync();
            // Populate cancels probes; detach this successful operation before refreshing controls.
            probeCancellation = null;
            Populate();
            ModelName.ItemsSource = result.Models.Select(x => x.Id).ToArray();
            ModelName.Text = result.Backend.Model;
            QuickStatus.Text = $"连接成功 · 已选择 {result.Backend.Model}，可以开始提问。";
            Status("DeepSeek 已连接并保存");
            OpenWorkspace();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(probeCancellation, cancellation)) return;
            var code = ConnectionDiagnostics.CodeFor(ex);
            QuickStatus.Text = ConnectionDiagnostics.Explain(code);
            QuickErrorCode.Text = code; QuickDetails.Visibility = Visibility.Visible;
        }
        finally
        {
            secret = "";
            if (ReferenceEquals(probeCancellation, cancellation)) probeCancellation = null;
            if (probeCancellation is null) SetProbeBusy(false);
        }
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await RunCommand("send");
    private void Pause_Click(object sender, RoutedEventArgs e) => EmergencyPause("紧急暂停已生效");
    private void ShowPanel_Click(object sender, RoutedEventArgs e) => OpenWorkspace(explicitHide: true);
    private bool CanHideControl() => hotkeys is not null && new[] { "settings", "pause", "exit" }.All(hotkeys.HasAction);
    private void HideControl_Click(object sender, RoutedEventArgs e)
    {
        if (!CanHideControl()) { Status("打开设置、暂停或退出的快捷键不可用；保留控制中心入口"); return; }
        reader.Show(); Hide();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void StopSync_Click(object sender, RoutedEventArgs e) { StopSync(); Status("Live Clipboard 已停止；当前请求与回答保留"); }
    private void StartSync_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        if (!clipboardListenerReady) { Status("剪贴板监听注册失败"); return; }
        var selected = config.Backends.FirstOrDefault(x => x.Id == config.SelectedBackendId);
        if (selected is null) { Status("先选择接收后端"); return; }
        var kinds = ImmutableHashSet.CreateBuilder<InputKind>();
        if (SyncText.IsChecked == true) kinds.Add(InputKind.ClipboardText);
        if (SyncImage.IsChecked == true) kinds.Add(InputKind.ClipboardImage);
        if (kinds.Count == 0) { Status("先选择授权输入范围"); return; }
        StopSync();
        grant = new(DateTimeOffset.UtcNow.AddMinutes(config.SyncMinutes), selected.Id, kinds.ToImmutable(), SyncSend.IsChecked == true);
        SyncSummary.Text = $"{selected.Name} · 截止 {grant.ExpiresAt.ToLocalTime():HH:mm:ss} · {(grant.AllowSend ? "允许自动发送" : "仅采集，不自动发送")}";
        UpdateMode(); Status("同步授权已生效；从下一次剪贴板变更开始");
    }
    private void BackendPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && BackendPicker.SelectedItem is Backend b && b.Id != config.SelectedBackendId)
        {
            StopSync(); // A live grant is bound to the backend selected when it was created.
            Mutate(() => config = config with { SelectedBackendId = b.Id });
        }
    }
    private void EditBackendPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading) LoadBackendEditor(); }
    private void LoadBackendEditor()
    {
        CancelProbe();
        var b = EditBackendPicker.SelectedItem as Backend;
        DeleteModelButton.IsEnabled = b is not null;
        ModelName.ItemsSource = null;
        BackendName.Text = b?.Name ?? ""; ModelName.Text = b?.Model ?? ""; Endpoint.Text = b?.Endpoint ?? "";
        ApiKey.Clear(); Vision.IsChecked = b?.SupportsImages ?? false; Streaming.IsChecked = b?.Stream ?? true;
        Loopback.IsChecked = b?.AllowLoopbackHttp ?? false; TimeoutSeconds.Text = (b?.TimeoutSeconds ?? 120).ToString();
        InputLimit.Text = ((b?.MaxInputBytes ?? 8_388_608) / 1_048_576).ToString();
        Temperature.Text = (b?.Temperature ?? 0.2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxTokens.Text = (b?.MaxTokens ?? Policy.DefaultOutputTokens).ToString(); MaxOutput.Text = (b?.MaxOutputChars ?? 1_000_000).ToString();
        ProxyModePicker.SelectedIndex = (int)(b?.ProxyMode ?? ProxyMode.Direct);
        ProxyAddressBox.Text = b?.ProxyAddress ?? "";
        SendTemperature.IsChecked = b?.SendTemperature ?? true;
        UseCompletionTokens.IsChecked = b?.TokenLimitParameter == TokenLimitParameter.MaxCompletionTokens;
        ThinkingModePicker.SelectedIndex = (int)(b?.ThinkingMode ?? ThinkingMode.Default);
        ConnectionStatus.Text = "尚未测试此后端";
    }
    private void NewBackend_Click(object sender, RoutedEventArgs e) { EditBackendPicker.SelectedItem = null; LoadBackendEditor(); }
    private void SaveBackend_Click(object sender, RoutedEventArgs e)
    {
        var secret = ApiKey.Password;
        StopSync(); // Endpoint/model/credential changes cross the live grant trust boundary.
        var old = EditBackendPicker.SelectedItem as Backend;
        var name = BackendName.Text.Trim(); var model = ModelName.Text.Trim(); var endpoint = Endpoint.Text.Trim();
        var vision = Vision.IsChecked == true; var loopback = Loopback.IsChecked == true; var streaming = Streaming.IsChecked == true;
        var timeout = TimeoutSeconds.Text; var limit = InputLimit.Text;
        var temperature = Temperature.Text; var tokens = MaxTokens.Text; var outputLimit = MaxOutput.Text;
        var proxyMode = (ProxyMode)ProxyModePicker.SelectedIndex;
        var proxyAddress = ProxyAddressBox.Text.Trim();
        var sendTemperature = SendTemperature.IsChecked == true;
        var tokenParameter = UseCompletionTokens.IsChecked == true ? TokenLimitParameter.MaxCompletionTokens : TokenLimitParameter.MaxTokens;
        var thinkingMode = (ThinkingMode)ThinkingModePicker.SelectedIndex;
        Mutate(() =>
        {
            var b = new Backend(old?.Id ?? Guid.NewGuid().ToString("N"), name, endpoint, model, old?.CredentialRef ?? "",
                vision, loopback, streaming, int.Parse(timeout), checked(int.Parse(limit) * 1_048_576), int.Parse(outputLimit),
                double.Parse(temperature, System.Globalization.CultureInfo.InvariantCulture), int.Parse(tokens),
                ProxyMode: proxyMode, ProxyAddress: proxyMode == ProxyMode.Custom ? proxyAddress : "",
                SendTemperature: sendTemperature, TokenLimitParameter: tokenParameter, ThinkingMode: thinkingMode);
            Policy.ValidateBackend(b);
            if (name.Length is < 1 or > 80) throw new SafeFailure("backend_name_invalid");
            if (secret.Length > 0) b = b with { CredentialRef = credentials.Save(secret) };
            config = config with { Backends = config.Backends.Where(x => x.Id != b.Id).Append(b).ToImmutableArray(), SelectedBackendId = b.Id };
            config.Save();
            if (secret.Length > 0 && old is not null && old.CredentialRef.Length > 0) credentials.Delete(old.CredentialRef);
        });
    }
    private void CancelProbe()
    {
        if (probeCancellation is null) return;
        var pending = probeCancellation;
        probeCancellation = null;
        pending.Cancel();
        SetProbeBusy(false);
        QuickStatus.Text = "连接测试已取消，未保存更改。";
        ConnectionStatus.Text = "连接测试已取消";
    }
    private void CancelProbe_Click(object sender, RoutedEventArgs e) => CancelProbe();
    private async void TestBackend_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        if (EditBackendPicker.SelectedItem is not Backend selected)
        {
            ConnectionStatus.Text = "请先保存并选择后端";
            return;
        }
        CancelProbe();
        using var cancellation = new CancellationTokenSource();
        probeCancellation = cancellation;
        SetProbeBusy(true);
        ConnectionStatus.Text = $"测试中 · {selected.ProxyMode} · 超时 {selected.TimeoutSeconds} 秒";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await BackendProbe.RunAsync(backend, selected, cancellation.Token);
            if (!ReferenceEquals(probeCancellation, cancellation)) return;
            ConnectionStatus.Text = result.Success
                ? $"连接测试通过 · {(result.ReceivedText ? "已收到完整文本响应" : "后端正常结束但未返回文本")} · {elapsed.Elapsed.TotalSeconds:F1} 秒"
                : $"测试失败 · {(result.HttpAccepted ? "HTTP 已成功，响应处理失败 · " : "")}{ConnectionDiagnostics.Describe(result.Code)}";
        }
        finally
        {
            if (ReferenceEquals(probeCancellation, cancellation))
            {
                probeCancellation = null;
                SetProbeBusy(false);
            }
        }
    }
    private void DeleteBackend_Click(object sender, RoutedEventArgs e)
    {
        if (EditBackendPicker.SelectedItem is not Backend b) return;
        if (config.Scenes.Any(s => s.BackendId == b.Id)) { Status("该后端仍被场景引用，请先调整场景后端"); return; }
        StopSync();
        CancelProbe();
        try
        {
            var candidate = config.WithoutBackend(b.Id);
            candidate.Save();
            config = candidate;
            Populate(); Status($"已删除模型配置：{b.Name}");
            // Shared credentials must remain available to other saved connections.
            if (!config.Backends.Any(other => other.CredentialRef == b.CredentialRef))
            {
                try { credentials.Delete(b.CredentialRef); }
                catch { Status("模型配置已删除，但凭据文件清理失败"); }
            }
        }
        catch (Exception ex) { Report(ex); }
    }
    private void RefreshTargets_Click(object sender, RoutedEventArgs e)
    {
        try { TargetPicker.ItemsSource = CaptureService.Enumerate(); }
        catch (Exception ex) { Report(ex); }
    }
    private void SaveTarget_Click(object sender, RoutedEventArgs e) => Mutate(() =>
    {
        var target = TargetPicker.SelectedItem as CaptureTarget ?? throw new SafeFailure("capture_target_missing");
        if (UseRegion.IsChecked == true)
        {
            if (target.Kind != CaptureKind.Monitor) throw new SafeFailure("region_requires_monitor");
            var parts = RegionBox.Text.Split(',').Select(int.Parse).ToArray();
            if (parts.Length != 4 || parts[0] < 0 || parts[1] < 0 || parts[2] <= 0 || parts[3] <= 0 ||
                (long)parts[0] + parts[2] > target.Bounds.Width || (long)parts[1] + parts[3] > target.Bounds.Height) throw new SafeFailure("region_invalid");
            var bounds = new PixelRect(checked(target.Bounds.X + parts[0]), checked(target.Bounds.Y + parts[1]), parts[2], parts[3]);
            target = target with { Kind = CaptureKind.Region, Bounds = bounds, MonitorBounds = target.Bounds, Label = $"矩形 · {target.Device} · {RegionBox.Text}" };
        }
        config = config with { Capture = target };
    });
    private void SavePipeline_Click(object sender, RoutedEventArgs e)
    {
        StopSync();
        Mutate(() =>
    {
        var steps = JsonSerializer.Deserialize<ImmutableArray<ProcessStep>>(PipelineBox.Text, Configuration.Json);
        var candidate = config with { Pipeline = new(Guid.NewGuid().ToString("N"), steps) };
        candidate.Validate(); config = candidate;
        });
    }
    internal Configuration BuildRoutingSettings()
    {
        string? SelectedRoute(ComboBox picker) => picker.SelectedValue is string id && id.Length > 0 ? id : null;
        var routing = new Routing(SelectedRoute(TextRoutePicker), SelectedRoute(ImageRoutePicker));
        if (routing.ImageBackendId is { } imageId && !config.Backends.Any(b => b.Id == imageId && b.SupportsImages))
            throw new SafeFailure("backend_has_no_vision");
        return config with { Routing = routing };
    }
    private void SaveRouting_Click(object sender, RoutedEventArgs e)
    {
        StopSync(); // Routing changes require a new explicit live authorization.
        Mutate(() => { var candidate = BuildRoutingSettings(); candidate.Validate(); config = candidate; });
    }
    // Each page saves only its own settings, never pending edits from another page.
    internal Configuration BuildHotkeySettings()
    {
        var bindings = ((IEnumerable<HotkeyEditorRow>)HotkeyRows.ItemsSource).Select(r => r.Binding()).ToArray();
        return config with { Hotkeys = bindings.Where(k => !k.Action.StartsWith("scene:")).ToImmutableArray(),
            Scenes = config.Scenes.Select(s => s with { Trigger = bindings.Single(k => k.Action == s.Action) }).ToImmutableArray(),
            ExclusiveHotkeysEnabled = ExclusiveReaderKeys.IsChecked == true };
    }
    internal Configuration BuildClipboardPreferences() => config with
    {
        AutoCopy = AutoCopy.IsChecked == true, ClearOwnedClipboardOnPause = ClearClipboard.IsChecked == true,
        SyncMinutes = int.Parse(SyncDuration.Text)
    };
    private void SaveHotkeys_Click(object sender, RoutedEventArgs e) => SaveScenarioSettings(BuildHotkeySettings);
    private void SavePreferences_Click(object sender, RoutedEventArgs e) => SaveScenarioSettings(BuildClipboardPreferences);
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { FileName = "superb-config.json", Filter = "JSON|*.json" };
            if (dialog.ShowDialog(this) == true)
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(config with { Backends = config.Backends.Select(x => x with { CredentialRef = "" }).ToImmutableArray(), Capture = null }, Configuration.Json));
        }
        catch (Exception ex) { Report(ex); }
    }
    private void Shutdown()
    {
        if (exiting) return;
        exiting = true;
        var layout = reader.Layout();
        EmergencyPause("已退出"); clipboardTimer.Stop(); guardTimer.Stop(); layoutSaveTimer.Stop();
        if (isolatedWorkspaceTest) { reader.Finish(); backend.Dispose(); return; }
        hotkeys?.Dispose();
        if (clipboardListenerReady) Native.RemoveClipboardFormatListener(handle);
        if (sessionListenerReady) Native.WTSUnRegisterSessionNotification(handle);
        try { if (!configLoadFailed) (config with { Panel = layout }).Save(); } catch { }
        reader.Finish(); backend.Dispose(); Application.Current.Shutdown();
    }
}
