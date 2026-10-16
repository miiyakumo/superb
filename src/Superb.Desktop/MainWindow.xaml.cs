using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    private ClipboardService? clipboard;
    private HotkeyManager? hotkeys;
    private SyncGrant? grant;
    private PreparedRequest? prepared;
    private RequestUpdate? latestUpdate;
    private RequestUpdate? completed;
    private Task<Payload>? captureTask;
    private long operation;
    private bool preparing, exiting, loading, pausedForProtection, sessionLocked, configLoadFailed;
    private bool clipboardListenerReady, sessionListenerReady;
    private DateTimeOffset lastAutoCapture = DateTimeOffset.MinValue;
    private nint handle;

    public MainWindow()
    {
        InitializeComponent();
        backend = new(credentials);
        coordinator = new(backend);
        protection = new(this, false);
        coordinator.Updated += OnUpdate;
        reader.Command += action => _ = RunCommand(action);
        reader.ProtectionLost += () => ProtectionFailed();
        SourceInitialized += InitializePlatform;
        clipboardTimer.Tick += async (_, _) =>
        {
            clipboardTimer.Stop();
            if (grant is null || DateTimeOffset.UtcNow >= grant.ExpiresAt) { StopSync(); return; }
            if (preparing || coordinator.State is RequestState.Connecting or RequestState.Waiting or RequestState.Streaming or RequestState.PolicyCheck ||
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
                if (coordinator.IsCurrent(update.Attempt)) reader.Render(update.Text, StateLabel(update));
            }
        };
        Closing += (_, _) => Shutdown();
    }
    private void InitializePlatform(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(this).Handle;
        var protectedOk = protection.Install();
        clipboard = new(handle);
        hotkeys = new(handle);
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        clipboardListenerReady = Native.AddClipboardFormatListener(handle);
        sessionListenerReady = Native.WTSRegisterSessionNotification(handle, 0);
        try { config = Configuration.Load(); }
        catch { configLoadFailed = true; Status("配置无法读取；已使用安全默认值，未覆盖原文件"); }
        Populate();
        reader.ApplyLayout(config.Panel);
        // Source initialization installs protection before any sensitive payload can be rendered.
        new WindowInteropHelper(reader).EnsureHandle();
        if (config.Panel.Visible && protectedOk) reader.Show();
        RegisterHotkeys();
        ProtectionLabel.Text = protectedOk && reader.Check()
            ? "属性已设置 · 当前配置未实测；尚无对外声明兼容的采集链路。"
            : "未验证 / 不支持 · 敏感显示与发送已阻止。";
        if (!protectedOk || !reader.Check()) ProtectionFailed();
        if (!sessionListenerReady) Status("系统会话通知注册失败；会话不可恢复");
        guardTimer.Start();
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
        RoutingBox.Text = JsonSerializer.Serialize(config.Routing, Configuration.Json);
        HotkeysBox.Text = JsonSerializer.Serialize(config.Hotkeys, Configuration.Json);
        AutoCopy.IsChecked = config.AutoCopy; Combine.IsChecked = config.CombineCaptureAndSend;
        ClearClipboard.IsChecked = config.ClearOwnedClipboardOnPause;
        SyncDuration.Text = config.SyncMinutes.ToString(); PanelFont.Text = config.Panel.FontSize.ToString();
        TargetLabel.Text = config.Capture?.Label ?? "未配置采集目标";
        BackendIds.Text = string.Join("\n", config.Backends.Select(x => $"{x.Name} → {x.Id}"));
        loading = false;
        LoadBackendEditor();
    }
    private void Status(string message) { StatusLabel.Text = message; reader.Status(message); }
    private static string StateLabel(RequestUpdate update) => (update.State switch
    {
        RequestState.PolicyCheck => "正在校验外发策略", RequestState.Connecting => "连接中", RequestState.Waiting => "等待响应",
        RequestState.Streaming => "生成中", RequestState.Completed => "已完成", RequestState.Cancelled => "已取消",
        RequestState.Failed => "失败 · " + update.Code, _ => update.State.ToString()
    }) + (update.Attempt.Request.ProcessingNotices.IsDefaultOrEmpty ? "" : $" · 已按规则跳过 {update.Attempt.Request.ProcessingNotices.Length} 个可选步骤");
    private void OnUpdate(RequestUpdate update)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnUpdate(update)); return; }
        if (!coordinator.IsCurrent(update.Attempt)) return;
        StatusLabel.Text = StateLabel(update);
        latestUpdate = update;
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
        if (grant is not null)
        {
            // Revoke in-flight preparation and output, not just the next clipboard notification.
            operation++; coordinator.Invalidate(); prepared = null; completed = null; latestUpdate = null; reader.Clear();
        }
        grant = null; clipboardTimer.Stop(); syncFilter.Reset(); SyncSummary.Text = "当前无授权"; UpdateMode();
    }
    private void UpdateMode() => ModeLabel.Text = $"{coordinator.Mode switch { RunState.Ready => "就绪", RunState.Locked => "锁定", RunState.ProtectionUnavailable => "保护不可用", _ => "已暂停" }} · {(grant is null ? "同步关闭" : "同步已授权")}";
    public void EmergencyPause(string reason, RunState mode = RunState.Paused)
    {
        operation++; coordinator.SetMode(mode); prepared = null; completed = null; latestUpdate = null;
        reader.Clear(); StopSync(); ApiKey.Clear();
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
    private async Task CaptureInput(bool screen, bool automatic = false)
    {
        if (!Ready() || preparing) return;
        var activeGrant = grant;
        if (automatic && (activeGrant is null || activeGrant.ExpiresAt <= DateTimeOffset.UtcNow)) { StopSync(); return; }
        var version = ++operation;
        preparing = true;
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
                captureTask = Task.Run(() => CaptureService.Capture(target));
                snapshot = new(Guid.NewGuid(), InputKind.Screen, await captureTask.WaitAsync(TimeSpan.FromSeconds(5)), sequence, DateTimeOffset.UtcNow);
                captureTask = null;
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
            coordinator.Invalidate(); completed = null; prepared = null; latestUpdate = null; reader.Clear();
            Status("本地处理中");
            var notices = new List<string>();
            var processed = await Task.Run(() => Preprocessor.Process(snapshot.Content, settings.Pipeline, new ImageProcessor(), notices.Add));
            if (version != operation || !Ready()) return;
            var route = settings.Routing.Select(processed, settings.SelectedBackendId, settings.Backends);
            if (automatic && !activeGrant!.Allows(snapshot.Kind, route.Id, DateTimeOffset.UtcNow, false)) throw new SafeFailure("sync_route_not_authorized");
            // Do not retain the raw source once local transformations have completed.
            snapshot = snapshot with { Content = processed };
            prepared = new(snapshot, processed, settings.Pipeline, route, notices.ToImmutableArray());
            Policy.Check(prepared);
            reader.Preview(processed);
            StatusLabel.Text = $"已准备 · {route.Name} / {route.Model} · 等待发送" + (notices.Count > 0 ? $" · {notices.Count} 个可选步骤已按规则跳过" : "");
            if (automatic) lastAutoCapture = DateTimeOffset.UtcNow;
            var send = automatic ? activeGrant!.AllowSend : settings.CombineCaptureAndSend;
            if (send) await SendPrepared(automatic, activeGrant);
        }
        catch (Exception ex) { if (version == operation) { prepared = null; Report(ex); } }
        finally
        {
            preparing = false;
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
        prepared = null; completed = null; latestUpdate = null; reader.Clear();
        await coordinator.RunAsync(request, automatic, authorization);
    }
    private async Task RunCommand(string action)
    {
        try
        {
            switch (action)
            {
                case "clipboard": await CaptureInput(false); break;
                case "capture": await CaptureInput(true); break;
                case "send": await SendPrepared(); break;
                case "retry":
                    if (Ready() && coordinator.Current is { } prior && coordinator.State is RequestState.Failed or RequestState.Completed)
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
                    operation++; coordinator.Invalidate(); prepared = null; completed = null; latestUpdate = null; reader.Clear(); Status("已取消；迟到输出将被丢弃"); break;
                case "pause": EmergencyPause("紧急暂停已生效"); break;
                case "pageDown": reader.Page(true); break;
                case "pageUp": reader.Page(false); break;
                case "settings": Show(); WindowState = WindowState.Normal; Activate(); break;
                case "exit": Close(); break;
            }
        }
        catch (Exception ex) { Report(ex); }
    }
    private void Report(Exception ex) => Status("操作未完成 · " + (ex is SafeFailure safe ? safe.Code : ex is TimeoutException ? "capture_timed_out" : "operation_failed"));
    private void Mutate(Action action)
    {
        try
        {
            EmergencyPause("配置已变更；请手动恢复会话");
            if (configLoadFailed)
            {
                File.Copy(Configuration.FilePath, Configuration.FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false);
                configLoadFailed = false;
            }
            action(); config.Save(); Populate();
        }
        catch (Exception ex) { Report(ex); }
    }
    private void RegisterHotkeys()
    {
        var failed = hotkeys!.Register(config.Hotkeys);
        if (failed.Count > 0) Status("快捷键冲突或无效（动作）：" + string.Join(", ", failed));
    }
    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (sessionLocked || !sessionListenerReady) { Status("系统会话尚未就绪"); return; }
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
        pausedForProtection = false; coordinator.SetMode(RunState.Ready); StopSync(); UpdateMode(); Status("已恢复；自动同步保持关闭");
    }
    private async void Clipboard_Click(object sender, RoutedEventArgs e) => await RunCommand("clipboard");
    private async void Capture_Click(object sender, RoutedEventArgs e) => await RunCommand("capture");
    private async void Send_Click(object sender, RoutedEventArgs e) => await RunCommand("send");
    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunCommand("retry");
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await RunCommand("cancel");
    private void Pause_Click(object sender, RoutedEventArgs e) => EmergencyPause("紧急暂停已生效");
    private void ShowPanel_Click(object sender, RoutedEventArgs e) { reader.Show(); reader.Check(); }
    private bool CanHideControl() => hotkeys is not null && new[] { "settings", "pause", "exit" }.All(hotkeys.HasAction);
    private void HideControl_Click(object sender, RoutedEventArgs e)
    {
        if (!CanHideControl()) { Status("打开设置、暂停或退出的快捷键不可用；保留控制中心入口"); return; }
        reader.Show(); Hide();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void StopSync_Click(object sender, RoutedEventArgs e) => StopSync();
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
            Mutate(() => config = config with { SelectedBackendId = b.Id });
    }
    private void EditBackendPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading) LoadBackendEditor(); }
    private void LoadBackendEditor()
    {
        var b = EditBackendPicker.SelectedItem as Backend;
        BackendName.Text = b?.Name ?? ""; ModelName.Text = b?.Model ?? ""; Endpoint.Text = b?.Endpoint ?? "";
        ApiKey.Clear(); Vision.IsChecked = b?.SupportsImages ?? false; Streaming.IsChecked = b?.Stream ?? true;
        Loopback.IsChecked = b?.AllowLoopbackHttp ?? false; TimeoutSeconds.Text = (b?.TimeoutSeconds ?? 120).ToString();
        InputLimit.Text = ((b?.MaxInputBytes ?? 8_388_608) / 1_048_576).ToString();
        Temperature.Text = (b?.Temperature ?? 0.2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxTokens.Text = (b?.MaxTokens ?? 4096).ToString(); MaxOutput.Text = (b?.MaxOutputChars ?? 200000).ToString();
    }
    private void NewBackend_Click(object sender, RoutedEventArgs e) { EditBackendPicker.SelectedItem = null; LoadBackendEditor(); }
    private void SaveBackend_Click(object sender, RoutedEventArgs e)
    {
        // Capture password before Mutate clears controls as part of its pause operation.
        var secret = ApiKey.Password;
        var old = EditBackendPicker.SelectedItem as Backend;
        var name = BackendName.Text.Trim(); var model = ModelName.Text.Trim(); var endpoint = Endpoint.Text.Trim();
        var vision = Vision.IsChecked == true; var loopback = Loopback.IsChecked == true; var streaming = Streaming.IsChecked == true;
        var timeout = TimeoutSeconds.Text; var limit = InputLimit.Text;
        var temperature = Temperature.Text; var tokens = MaxTokens.Text; var outputLimit = MaxOutput.Text;
        Mutate(() =>
        {
            var b = new Backend(old?.Id ?? Guid.NewGuid().ToString("N"), name, endpoint, model, old?.CredentialRef ?? "",
                vision, loopback, streaming, int.Parse(timeout), checked(int.Parse(limit) * 1_048_576), int.Parse(outputLimit),
                double.Parse(temperature, System.Globalization.CultureInfo.InvariantCulture), int.Parse(tokens));
            Policy.ValidateBackend(b);
            if (name.Length is < 1 or > 80) throw new SafeFailure("backend_name_invalid");
            if (secret.Length > 0) b = b with { CredentialRef = credentials.Save(secret) };
            config = config with { Backends = config.Backends.Where(x => x.Id != b.Id).Append(b).ToImmutableArray(), SelectedBackendId = b.Id };
            config.Save();
            if (secret.Length > 0 && old is not null && old.CredentialRef.Length > 0) credentials.Delete(old.CredentialRef);
        });
    }
    private void DeleteBackend_Click(object sender, RoutedEventArgs e)
    {
        if (EditBackendPicker.SelectedItem is not Backend b) return;
        Mutate(() =>
        {
            var remaining = config.Backends.Where(x => x.Id != b.Id).ToImmutableArray();
            config = config with { Backends = remaining, SelectedBackendId = remaining.FirstOrDefault()?.Id ?? "",
                Routing = new(config.Routing.TextBackendId == b.Id ? null : config.Routing.TextBackendId, config.Routing.ImageBackendId == b.Id ? null : config.Routing.ImageBackendId) };
            config.Save(); credentials.Delete(b.CredentialRef);
        });
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
    private void SavePipeline_Click(object sender, RoutedEventArgs e) => Mutate(() =>
    {
        var steps = JsonSerializer.Deserialize<ImmutableArray<ProcessStep>>(PipelineBox.Text, Configuration.Json);
        var routing = JsonSerializer.Deserialize<Routing>(RoutingBox.Text, Configuration.Json) ?? throw new SafeFailure("routing_invalid");
        var candidate = config with { Pipeline = new(Guid.NewGuid().ToString("N"), steps), Routing = routing };
        candidate.Validate(); config = candidate;
    });
    private void SavePreferences_Click(object sender, RoutedEventArgs e) => Mutate(() =>
    {
        var keys = JsonSerializer.Deserialize<ImmutableArray<Hotkey>>(HotkeysBox.Text, Configuration.Json);
        string[] required = ["capture", "clipboard", "toggle", "copy", "cancel", "pause", "settings", "exit"];
        if (keys.IsDefault || required.Any(action => !keys.Any(x => x.Action == action)) || keys.Select(x => x.Action).Distinct().Count() != keys.Length)
            throw new SafeFailure("hotkey_required_action_missing");
        var candidate = config with { Hotkeys = keys, AutoCopy = AutoCopy.IsChecked == true, CombineCaptureAndSend = Combine.IsChecked == true,
            ClearOwnedClipboardOnPause = ClearClipboard.IsChecked == true, SyncMinutes = int.Parse(SyncDuration.Text),
            Panel = reader.Layout() with { FontSize = double.Parse(PanelFont.Text) } };
        candidate.Validate(); config = candidate; reader.ApplyLayout(config.Panel); RegisterHotkeys();
    });
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
        EmergencyPause("已退出"); clipboardTimer.Stop(); guardTimer.Stop();
        hotkeys?.Dispose();
        if (clipboardListenerReady) Native.RemoveClipboardFormatListener(handle);
        if (sessionListenerReady) Native.WTSUnRegisterSessionNotification(handle);
        try { if (!configLoadFailed) (config with { Panel = layout }).Save(); } catch { }
        reader.Finish(); backend.Dispose(); Application.Current.Shutdown();
    }
}
