using System.Collections.Immutable;
using System.IO;
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
    private readonly DispatcherTimer guardTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer layoutSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ClipboardService? clipboard;
    private HotkeyManager? hotkeys;
    private RequestUpdate? latestUpdate;
    private RequestUpdate? completed;
    private Task<Payload>? captureTask;
    private CancellationTokenSource? probeCancellation;
    private long operation;
    private bool preparing, exiting, exitRequested, loading, pausedForProtection, sessionLocked, configLoadFailed;
    private bool sessionListenerReady;
    private bool settingsStatusExceptional;
    private string? hotkeyRegistrationWarning;
    private nint handle;
    private readonly bool isolatedWorkspaceTest;
    private readonly Func<CaptureTarget, Task<Payload>> captureScreenshot;
    private readonly Func<(InputSnapshot Snapshot, bool OwnOutput)> readWorkspaceClipboard;
    internal ReaderWindow WorkspaceWindow => reader;
    internal Coordinator WorkspaceCoordinator => coordinator;

    public MainWindow() : this(null, null) { }
    // Isolated synthetic integration host: no user configuration, global hotkeys, clipboard reads or persistence.
    internal MainWindow(Configuration? testConfiguration, IBackendClient? testBackend,
        Func<CaptureTarget, Task<Payload>>? testCapture = null,
        string? testDiagnosticDirectory = null, Func<(InputSnapshot Snapshot, bool OwnOutput)>? testClipboardRead = null)
    {
        isolatedWorkspaceTest = testConfiguration is not null && testBackend is not null;
        captureScreenshot = isolatedWorkspaceTest && testCapture is not null
            ? testCapture : target => Task.Run(() => CaptureService.Capture(target));
        readWorkspaceClipboard = isolatedWorkspaceTest && testClipboardRead is not null ? testClipboardRead : () => clipboard!.Read();
        if (testConfiguration is not null) config = testConfiguration;
        InitializeComponent();
        backend = new(credentials);
        coordinator = new(testBackend ?? backend);
        var diagnosticDirectory = isolatedWorkspaceTest ? testDiagnosticDirectory : WorkspaceDiagnosticLog.DefaultDirectory;
        workspaceDiagnosticLog = diagnosticDirectory is null ? null : new(diagnosticDirectory);
        TraceWorkspace(WorkspaceEvent.ApplicationStarted);
        DiagnosticsLocation.Text = WorkspaceDiagnosticLog.DefaultDirectory;
        protection = new(this, false);
        protection.AllowMouseActivation = () => !IsVisible || PresentationSource.FromVisual(this)?.CompositionTarget is null ||
            (Native.GetCursorPos(out var point) &&
                CanActivateSettingsTarget(InputHitTest(PointFromScreen(new Point(point.X, point.Y))) as DependencyObject));
        coordinator.Updated += OnUpdate;
        reader.Command += action => _ = RunCommand(action);
        reader.LayoutChanged += () =>
        {
            config = config with { Panel = reader.Layout() };
            SyncAppearanceControls();
            if (!isolatedWorkspaceTest && !configLoadFailed && !exiting) { layoutSaveTimer.Stop(); layoutSaveTimer.Start(); }
        };
        layoutSaveTimer.Tick += (_, _) =>
        {
            layoutSaveTimer.Stop();
            try { config.Save(); } catch { Status("外观已调整，但设置保存失败；本次会话仍可使用", exceptional: true); }
        };
        reader.ProtectionLost += () => ProtectionFailed();
        SourceInitialized += InitializePlatform;
        guardTimer.Tick += (_, _) =>
        {
            if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
            if (latestUpdate is { } update)
            {
                latestUpdate = null;
                if (coordinator.IsCurrent(update.Attempt))
                {
                    reader.Render(update.Text, StateLabel(update));
                }
            }
        };
        Closing += (_, e) =>
        {
            if (exitRequested) { Shutdown(); return; }
            e.Cancel = true;
            CloseSettings();
        };
        InitializeAppearanceSettings();
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
        sessionListenerReady = Native.WTSRegisterSessionNotification(handle, 0);
        try { config = Configuration.Load(); }
        catch { configLoadFailed = true; Status("配置无法读取；已使用安全默认值，未覆盖原文件", exceptional: true); }
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
        else if (!sessionListenerReady) { UpdateMode(); Status("系统会话通知注册失败；保持暂停", exceptional: true); }
        else
        {
            pausedForProtection = false;
            coordinator.SetMode(RunState.Ready);
            UpdateMode();
            if (!configLoadFailed)
                Status(config.Backends.IsDefaultOrEmpty
                    ? "新建模型配置，填写服务地址、模型和密钥后即可开始"
                    : "已就绪 · 可使用场景入口或已启用的快捷键");
        }
        guardTimer.Start();
        if (!config.Backends.IsDefaultOrEmpty && protectedOk && readerOk && sessionListenerReady)
            Dispatcher.BeginInvoke(() => OpenWorkspace());
    }
    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x312) { handled = true; if (hotkeys?.GetAction((int)wParam) is { } action) _ = RunCommand(action); }
        if (msg == 0x2B1)
        {
            if ((int)wParam is 7 or 6 or 4 or 2) // lock, logoff, remote/console disconnect
            {
                sessionLocked = true; PauseWithReason("系统会话已锁定或断开", WorkspaceReason.SessionLocked, RunState.Locked);
            }
            else if ((int)wParam is 8 or 5 or 3 or 1)
            {
                sessionLocked = false; PauseWithReason("会话已返回；需要手动恢复", WorkspaceReason.SessionReturned, exceptional: true);
            }
        }
        if (msg == 0x218 && (int)wParam == 4) PauseWithReason("系统挂起；需要手动恢复", WorkspaceReason.SystemSuspend, exceptional: true);
        if (msg == 0x7E) PauseWithReason("显示配置变化；已暂停，请恢复后重新截图", WorkspaceReason.DisplayChanged, exceptional: true);
        if (msg == 0x31E && !protection.Check()) ProtectionFailed();
        return 0;
    }
    private void Populate(string? editId = null)
    {
        loading = true;
        var priorId = editId ?? EditingBackend?.Id ?? config.SelectedBackendId;
        EditBackendPicker.ItemsSource = config.Backends.Select(b => new ModelConnectionRow(b, b.Id == config.SelectedBackendId)).ToArray();
        EditBackendPicker.SelectedItem = ((ModelConnectionRow[])EditBackendPicker.ItemsSource).FirstOrDefault(row => row.Id == priorId);
        PopulateScenesAndKeys();
        AutoCopy.IsChecked = config.AutoCopy;
        ClearClipboard.IsChecked = config.ClearOwnedClipboardOnPause;
        SavedConnections.Visibility = config.Backends.IsDefaultOrEmpty ? Visibility.Collapsed : Visibility.Visible;
        var selected = config.Backends.FirstOrDefault(b => b.Id == config.SelectedBackendId);
        ModelSummary.Text = selected is null ? "先配置一个模型连接，即可开始提问。" : $"当前默认：{selected.Name} · {selected.Model}";
        loading = false;
        SyncAppearanceControls();
        LoadBackendEditor();
        RefreshWorkspace();
    }
    private void Status(string message, bool exceptional = false) { StatusLabel.Text = message; reader.Status(message); SetError(null, exceptional); RefreshWorkspace(); }
    private void SetError(string? code, bool exceptional = false)
    {
        settingsStatusExceptional = exceptional || code is not null;
        ErrorCode.Text = code ?? "";
        ErrorDetails.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        if (code is null) ErrorDetails.IsExpanded = false;
        RefreshSettingsStatus();
    }
    private void RefreshSettingsStatus()
    {
        // Health failures remain visible when ordinary workspace progress updates arrive.
        var warning = coordinator.Mode switch
        {
            RunState.ProtectionUnavailable => "保护不可用；敏感内容已遮蔽，请检查诊断后恢复",
            RunState.Locked => "系统会话已锁定或断开；返回后需要手动恢复",
            _ when configLoadFailed => "配置无法读取；已使用安全默认值，未覆盖原文件",
            _ when !isolatedWorkspaceTest && handle != 0 && !sessionListenerReady => "系统会话通知注册失败；保持暂停",
            _ => hotkeyRegistrationWarning
        };
        SettingsStatusCard.Visibility = settingsStatusExceptional || warning is not null ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel.Text = coordinator.Mode switch
        {
            RunState.Locked => "锁定", RunState.ProtectionUnavailable => "保护不可用",
            RunState.Paused => "已暂停", _ => "需要处理"
        };
        if (warning is not null && !settingsStatusExceptional) StatusLabel.Text = warning;
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
    });
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
    private void UpdateMode()
    {
        ResumeButton.Visibility = coordinator.Mode == RunState.Ready ? Visibility.Collapsed : Visibility.Visible;
        SettingsPauseButton.Visibility = coordinator.Mode == RunState.Ready ? Visibility.Visible : Visibility.Collapsed;
        RefreshSettingsStatus();
        RefreshWorkspace();
    }
    public void EmergencyPause(string reason, RunState mode = RunState.Paused, bool exceptional = false)
        => PauseWithReason(reason, WorkspaceReason.EmergencyPause, mode, exceptional);
    internal void PauseWithReason(string reason, WorkspaceReason diagnosticReason, RunState mode = RunState.Paused, bool exceptional = false)
    {
        var clearId = Guid.NewGuid(); var previousTaskId = workspaceTaskId;
        TraceWorkspace(WorkspaceEvent.TaskClearing, diagnosticReason, operationId: clearId);
        captureSelectionCancellation?.Cancel();
        CancelProbe();
        operation++; preparing = false; coordinator.SetMode(mode); completed = null; latestUpdate = null;
        ordinaryWorkspace.Clear(); workspaceTaskId = Guid.NewGuid();
        TraceWorkspace(WorkspaceEvent.TaskCleared, diagnosticReason, operationId: clearId, previousTaskId: previousTaskId);
        reader.Clear(); ClearOutput(); ApiKey.Clear();
        if (reader.Check()) reader.SetClickThrough(false);
        if (config.ClearOwnedClipboardOnPause) { try { clipboard?.ClearOwned(); } catch { /* Clear best effort only our own data. */ } }
        UpdateMode(); Status(reason, exceptional);
    }
    private void ProtectionFailed()
    {
        if (pausedForProtection) return;
        pausedForProtection = true;
        PauseWithReason("保护不可用；敏感内容已遮蔽", WorkspaceReason.ProtectionLost, RunState.ProtectionUnavailable);
        ProtectionLabel.Text = "未验证 / 不支持 · 敏感显示已关闭";
    }
    private bool Ready()
    {
        if (coordinator.Mode != RunState.Ready) { Status("请先在控制中心恢复会话"); return false; }
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return false; }
        return true;
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
                if (!Working) { RemoveQueuedMaterial(itemId, WorkspaceReason.RemoveItem); reader.SetInputs(workspace.Items); Status("已从队列移除"); }
                return;
            }
            switch (action)
            {
                case "stageClipboard": await StageClipboard(); break;
                case "stageCapture": await StageScreenshot(true); break;
                case "captureScreen": await StageScreenshot(false); break;
                case "captureScreenSend": await StageScreenshot(false, sendImmediately: true); break;
                case "deleteCapture":
                    if ((!Working) && workspace.Items.LastOrDefault() is { } last) { RemoveQueuedMaterial(last.Snapshot.Id, WorkspaceReason.RemoveLast); reader.SetInputs(workspace.Items); Status("已移除队列最后一项"); }
                    break;
                case "selectScene": SelectWorkspaceScene(reader.SelectedSceneId); break;
                case "removeInput":
                    if (!Working) { ClearQueuedMaterials(WorkspaceReason.RemoveAll); reader.ClearInput(); Status("已移除输入"); RefreshWorkspace(); }
                    break;
                case "workspaceCopy": CopyWorkspaceAnswer(); break;
                case "newTask": NewWorkspaceTask(); break;
                case "through":
                    if (!Ready()) break;
                    if (reader.ClickThrough) reader.SetClickThrough(false);
                    else if (hotkeys?.HasAction("through") == true && CanHideControl()) reader.SetClickThrough(true);
                    else Status("穿透未开启：请先修复穿透、设置、暂停和退出快捷键", exceptional: true);
                    break;
                case "resume": Resume_Click(this, new RoutedEventArgs()); break;
                case "clipboard": await StageClipboard(); break;
                case "capture": await StageScreenshot(true); break;
                case "send": await SendWorkspace(); break;
                case "retryDirect": await RetryDirectAnswer(); break;
                case "retry":
                    if (Ready() && !Working && coordinator.Current is { } prior && coordinator.State is RequestState.Failed or RequestState.Completed or RequestState.Cancelled)
                    {
                        await SendPrepared(prior.Request); // Explicit retry; new attempt ID, frozen target and body.
                    }
                    else Status("没有可重试的已结束请求");
                    break;
                case "toggle":
                    if (reader.IsVisible)
                    {
                        if (!IsVisible && !CanHideControl()) { Status("关键快捷键不可用；请先打开控制中心", exceptional: true); break; }
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
                case "exit": ExitApplication(); break;
            }
        }
        catch (Exception ex)
        {
            Report(ex);
        }
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
        var failed = hotkeys!.Register(config.AllHotkeys());
        if (reader.ClickThrough && !hotkeys.HasAction("through")) reader.SetClickThrough(false);
        HotkeyStatus.Text = failed.Count > 0 ? "未启用的动作：" + string.Join(", ", failed)
            : "已启用的快捷键注册成功";
        hotkeyRegistrationWarning = failed.Count > 0 ? "快捷键冲突或无效（动作）：" + string.Join(", ", failed) : null;
        if (hotkeyRegistrationWarning is not null) Status(hotkeyRegistrationWarning, exceptional: true);
        else RefreshSettingsStatus();
        RefreshWorkspace();
    }
    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (sessionLocked || !sessionListenerReady) { Status("系统会话尚未就绪", exceptional: true); return; }
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
        pausedForProtection = false; coordinator.SetMode(RunState.Ready); UpdateMode(); Status("已恢复");
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => EmergencyPause("紧急暂停已生效");
    private void ShowPanel_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private bool CanHideControl() => hotkeys is not null && new[] { "settings", "pause", "exit" }.All(hotkeys.HasAction);
    private void CloseSettings()
    {
        CancelProbe();
        // Keep a reachable settings button even when recovery hotkeys are unavailable.
        // Do not pause, clear the workspace, or dispose its active model request.
        reader.Show();
        if (reader.ClickThrough) reader.SetClickThrough(false);
        Hide();
    }
    private void MinimizeSettings_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeSettings_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    private void HideControl_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
    internal void ExitApplication()
    {
        exitRequested = true;
        Close();
    }
    private void DeleteBackend_Click(object sender, RoutedEventArgs e)
    {
        if (EditingBackend is not { } b) return;
        if (config.Scenes.Any(s => s.BackendId == b.Id)) { Status("该后端仍被场景引用，请先调整场景后端", exceptional: true); return; }
        CancelProbe();
        try
        {
            var candidate = config.WithoutBackend(b.Id);
            if (!isolatedWorkspaceTest) candidate.Save();
            config = candidate;
            Populate(); Status($"已删除模型配置：{b.Name}");
            // Shared credentials must remain available to other saved connections.
            if (!config.Backends.Any(other => other.CredentialRef == b.CredentialRef))
            {
                try { credentials.Delete(b.CredentialRef); }
                catch { Status("模型配置已删除，但凭据文件清理失败", exceptional: true); }
            }
        }
        catch (Exception ex) { Report(ex); }
    }
    // Each page saves only its own settings, never pending edits from another page.
    internal Configuration BuildHotkeySettings()
    {
        var bindings = ((IEnumerable<HotkeyEditorRow>)HotkeyRows.ItemsSource).Select(r => r.Binding()).ToArray();
        return config with { Hotkeys = bindings.Where(k => !k.Action.StartsWith("scene:")).ToImmutableArray(),
            Scenes = config.Scenes.Select(s => s with { Trigger = bindings.Single(k => k.Action == s.Action) }).ToImmutableArray() };
    }
    internal Configuration BuildClipboardPreferences() => config with
    {
        AutoCopy = AutoCopy.IsChecked == true, ClearOwnedClipboardOnPause = ClearClipboard.IsChecked == true
    };
    private void SaveHotkeys_Click(object sender, RoutedEventArgs e) => SaveScenarioSettings(BuildHotkeySettings);
    private void SavePreferences_Click(object sender, RoutedEventArgs e) => SaveScenarioSettings(BuildClipboardPreferences);
    private void Shutdown()
    {
        if (exiting) return;
        exiting = true;
        var layout = reader.Layout();
        PauseWithReason("已退出", WorkspaceReason.Exit); guardTimer.Stop(); layoutSaveTimer.Stop();
        if (isolatedWorkspaceTest) { reader.Finish(); backend.Dispose(); return; }
        hotkeys?.Dispose();
        if (sessionListenerReady) Native.WTSUnRegisterSessionNotification(handle);
        try { if (!configLoadFailed) (config with { Panel = layout }).Save(); } catch { }
        reader.Finish(); backend.Dispose(); Application.Current.Shutdown();
    }
}
