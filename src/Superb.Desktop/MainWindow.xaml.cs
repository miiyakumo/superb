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
    private bool preparing, exiting, loading, pausedForProtection, sessionLocked, configLoadFailed;
    private bool sessionListenerReady;
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
        reader.DraftChanged += RefreshWorkspace;
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
        if (msg == 0x7E) EmergencyPause("显示配置变化；已暂停，请恢复后重新截图");
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
        PopulateScenesAndKeys();
        ExclusiveReaderKeys.IsChecked = config.ExclusiveHotkeysEnabled;
        AutoCopy.IsChecked = config.AutoCopy;
        ClearClipboard.IsChecked = config.ClearOwnedClipboardOnPause;
        DefaultConnection.Visibility = config.Backends.IsDefaultOrEmpty ? Visibility.Collapsed : Visibility.Visible;
        QuickSetup.IsExpanded = config.Backends.IsDefaultOrEmpty;
        BackendSettings.IsExpanded = !config.Backends.IsDefaultOrEmpty;
        var selected = BackendPicker.SelectedItem as Backend;
        ModelSummary.Text = selected is null ? "先连接一个模型，即可开始提问。" : $"{selected.Name} · {selected.Model}";
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
        ModeLabel.Text = $"{coordinator.Mode switch { RunState.Ready => "就绪", RunState.Locked => "锁定", RunState.ProtectionUnavailable => "保护不可用", _ => "已暂停" }}";
        ResumeButton.Visibility = coordinator.Mode == RunState.Ready ? Visibility.Collapsed : Visibility.Visible;
        RefreshWorkspace();
    }
    public void EmergencyPause(string reason, RunState mode = RunState.Paused)
    {
        captureSelectionCancellation?.Cancel();
        CancelProbe();
        operation++; preparing = false; coordinator.SetMode(mode); completed = null; latestUpdate = null;
        workspace.Clear(); reader.Clear(); ClearOutput(); ApiKey.Clear(); QuickApiKey.Clear();
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
                if (!Working) { workspace.Remove(itemId); reader.SetInputs(workspace.Items); Status("已从队列移除"); }
                return;
            }
            switch (action)
            {
                case "stageClipboard": await StageClipboard(); break;
                case "stageCapture": await StageScreenshot(true); break;
                case "captureScreen": await StageScreenshot(false); break;
                case "deleteCapture":
                    if (!Working && workspace.Items.LastOrDefault() is { } last) { workspace.Remove(last.Snapshot.Id); reader.SetInputs(workspace.Items); Status("已移除队列最后一项"); }
                    break;
                case "selectScene": SelectWorkspaceScene(reader.SelectedSceneId); break;
                case "removeInput":
                    if (!Working) { workspace.Clear(); reader.ClearInput(); Status("已移除输入"); RefreshWorkspace(); }
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
            (ProxyMode)QuickProxyMode.SelectedIndex, QuickProxyAddress.Text.Trim()) with
            { MaxInputBytes = existing?.MaxInputBytes ?? 8_388_608, MaxOutputChars = existing?.MaxOutputChars ?? 1_000_000 };
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
    private void Pause_Click(object sender, RoutedEventArgs e) => EmergencyPause("紧急暂停已生效");
    private void ShowPanel_Click(object sender, RoutedEventArgs e) => OpenWorkspace(explicitHide: true);
    private bool CanHideControl() => hotkeys is not null && new[] { "settings", "pause", "exit" }.All(hotkeys.HasAction);
    private void HideControl_Click(object sender, RoutedEventArgs e)
    {
        if (!CanHideControl()) { Status("打开设置、暂停或退出的快捷键不可用；保留控制中心入口"); return; }
        reader.Show(); Hide();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void BackendPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && BackendPicker.SelectedItem is Backend b && b.Id != config.SelectedBackendId)
        {
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
        Temperature.Text = (b?.Temperature ?? 0.2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxTokens.Text = (b?.MaxTokens ?? Policy.DefaultOutputTokens).ToString();
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
        var old = EditBackendPicker.SelectedItem as Backend;
        Mutate(() =>
        {
            var b = BuildBackendSettings();
            if (secret.Length > 0) b = b with { CredentialRef = credentials.Save(secret) };
            config = config with { Backends = config.Backends.Where(x => x.Id != b.Id).Append(b).ToImmutableArray(), SelectedBackendId = b.Id };
            config.Save();
            if (secret.Length > 0 && old is not null && old.CredentialRef.Length > 0 &&
                !config.Backends.Any(other => other.CredentialRef == old.CredentialRef)) credentials.Delete(old.CredentialRef);
        });
    }
    internal Backend BuildBackendSettings()
    {
        var old = EditBackendPicker.SelectedItem as Backend;
        var proxyMode = (ProxyMode)ProxyModePicker.SelectedIndex;
        var b = new Backend(old?.Id ?? Guid.NewGuid().ToString("N"), BackendName.Text.Trim(), Endpoint.Text.Trim(),
            ModelName.Text.Trim(), old?.CredentialRef ?? "", Vision.IsChecked == true, Loopback.IsChecked == true,
            Streaming.IsChecked == true, int.Parse(TimeoutSeconds.Text), old?.MaxInputBytes ?? 8_388_608,
            old?.MaxOutputChars ?? 1_000_000, double.Parse(Temperature.Text, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(MaxTokens.Text), ProxyMode: proxyMode, ProxyAddress: proxyMode == ProxyMode.Custom ? ProxyAddressBox.Text.Trim() : "",
            SendTemperature: SendTemperature.IsChecked == true,
            TokenLimitParameter: UseCompletionTokens.IsChecked == true ? TokenLimitParameter.MaxCompletionTokens : TokenLimitParameter.MaxTokens,
            ThinkingMode: (ThinkingMode)ThinkingModePicker.SelectedIndex);
        Policy.ValidateBackend(b);
        if (b.Name.Length is < 1 or > 80) throw new SafeFailure("backend_name_invalid");
        return b;
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
        AutoCopy = AutoCopy.IsChecked == true, ClearOwnedClipboardOnPause = ClearClipboard.IsChecked == true
    };
    private void SaveHotkeys_Click(object sender, RoutedEventArgs e) => SaveScenarioSettings(BuildHotkeySettings);
    private void SavePreferences_Click(object sender, RoutedEventArgs e) => SaveScenarioSettings(BuildClipboardPreferences);
    private void Shutdown()
    {
        if (exiting) return;
        exiting = true;
        var layout = reader.Layout();
        EmergencyPause("已退出"); guardTimer.Stop(); layoutSaveTimer.Stop();
        if (isolatedWorkspaceTest) { reader.Finish(); backend.Dispose(); return; }
        hotkeys?.Dispose();
        if (sessionListenerReady) Native.WTSUnRegisterSessionNotification(handle);
        try { if (!configLoadFailed) (config with { Panel = layout }).Save(); } catch { }
        reader.Finish(); backend.Dispose(); Application.Current.Shutdown();
    }
}
