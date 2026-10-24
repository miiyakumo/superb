using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Superb.Core;

namespace Superb.Desktop;

public partial class MainWindow
{
    private sealed class ModelConnectionRow(Backend backend, bool isDefault) : System.ComponentModel.INotifyPropertyChanged
    {
        public Backend Backend { get; } = backend;
        public string Id => Backend.Id;
        public string Name => Backend.Name;
        public string Model => Backend.Model;
        private bool currentDefault = isDefault;
        public bool IsDefault { get => currentDefault; set { currentDefault = value; PropertyChanged?.Invoke(this, new(nameof(IsDefault))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    internal Backend? EditingBackend => (EditBackendPicker.SelectedItem as ModelConnectionRow)?.Backend;
    private void SetDefaultBackend_Click(object sender, RoutedEventArgs e)
    {
        if (EditingBackend is not { } selected) return;
        try
        {
            var next = config with { SelectedBackendId = selected.Id }; next.Validate();
            if (!isolatedWorkspaceTest) next.Save();
            config = next;
            foreach (ModelConnectionRow row in EditBackendPicker.Items) row.IsDefault = row.Id == selected.Id;
            SetDefaultModelButton.IsEnabled = false;
            ModelSummary.Text = $"当前默认：{selected.Name} · {selected.Model}";
            Status("默认连接已更新");
        }
        catch (Exception ex) { Report(ex); }
    }
    private sealed class TemporaryCredential(string secret) : ICredentialStore, IDisposable
    {
        private string value = secret;
        public string Read(string reference) => value;
        public void Dispose() => value = "";
    }

    private void EditBackendPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) LoadBackendEditor();
    }

    private void LoadBackendEditor()
    {
        CancelProbe();
        var b = EditingBackend;
        SetDefaultModelButton.IsEnabled = b is not null && b.Id != config.SelectedBackendId;
        DeleteModelButton.IsEnabled = b is not null;
        ModelEditorHeading.Text = b is null ? "新建模型配置" : $"编辑：{b.Name}";
        ModelName.ItemsSource = null;
        BackendName.Text = b?.Name ?? "";
        ModelName.Text = b?.Model ?? "";
        Endpoint.Text = b?.Endpoint ?? "";
        ApiKey.Clear();
        ApiKeyHint.Text = string.IsNullOrEmpty(b?.CredentialRef)
            ? "按服务要求填写；无认证的本机服务可留空。密钥加密保存在当前 Windows 用户下。"
            : "已保存密钥；留空保留已有密钥，输入新密钥可替换。密钥加密保存在当前 Windows 用户下。";
        Vision.IsChecked = b?.SupportsImages ?? false;
        Streaming.IsChecked = b?.Stream ?? true;
        Loopback.IsChecked = b?.AllowLoopbackHttp ?? false;
        TimeoutSeconds.Text = (b?.TimeoutSeconds ?? 300).ToString();
        Temperature.Text = (b?.Temperature ?? 0.2).ToString(CultureInfo.InvariantCulture);
        MaxTokens.Text = (b?.MaxTokens ?? Policy.DefaultOutputTokens).ToString();
        ProxyModePicker.SelectedIndex = (int)(b?.ProxyMode ?? ProxyMode.Direct);
        ProxyAddressBox.Text = b?.ProxyAddress ?? "";
        SendTemperature.IsChecked = b?.SendTemperature ?? true;
        UseCompletionTokens.IsChecked = b?.TokenLimitParameter == TokenLimitParameter.MaxCompletionTokens;
        ThinkingModePicker.SelectedIndex = (int)(b?.ThinkingMode ?? ThinkingMode.Default);
        UpdateThinkingSettings();
        ConnectionStatus.Text = "填写配置后可直接测试；测试通过后仍需点击保存。";
        ModelEditorScroll.ScrollToTop();
    }

    private void Endpoint_TextChanged(object sender, TextChangedEventArgs e) => UpdateThinkingSettings();
    private void UpdateThinkingSettings()
    {
        // TextChanged may run before the remaining XAML controls have been created.
        if (ThinkingSettings is null || ThinkingModePicker is null) return;
        var official = false;
        try { official = DeepSeekSetup.IsOfficialEndpoint(BackendSetup.NormalizeEndpoint(Endpoint.Text)); }
        catch (SafeFailure) { }
        ThinkingSettings.Visibility = official ? Visibility.Visible : Visibility.Collapsed;
        if (!official) ThinkingModePicker.SelectedIndex = (int)ThinkingMode.Default;
    }

    private void NewBackend_Click(object sender, RoutedEventArgs e)
    {
        EditBackendPicker.SelectedItem = null;
        LoadBackendEditor();
        (BackendName.Text, Endpoint.Text) = ConnectionPresetPicker.SelectedIndex switch
        {
            1 => ("OpenAI", "https://api.openai.com/v1/chat/completions"),
            2 => ("DeepSeek", DeepSeekSetup.Endpoint),
            3 => ("本机模型", "http://127.0.0.1:11434/v1/chat/completions"),
            _ => ("", "")
        };
        Loopback.IsChecked = ConnectionPresetPicker.SelectedIndex == 3;
        ConnectionStatus.Text = "正在新建配置 · 填写名称、模型 ID 和服务地址后，可测试或保存。";
        BackendName.Focus();
        BackendName.SelectAll();
        ModelEditorScroll.ScrollToTop();
    }

    internal Backend BuildBackendSettings()
    {
        var old = EditingBackend;
        if (string.IsNullOrWhiteSpace(BackendName.Text) || BackendName.Text.Trim().Length > 80)
            throw new SafeFailure("backend_name_invalid");
        if (string.IsNullOrWhiteSpace(ModelName.Text)) throw new SafeFailure("backend_model_required");
        if (string.IsNullOrWhiteSpace(Endpoint.Text)) throw new SafeFailure("backend_endpoint_required");
        if (!int.TryParse(TimeoutSeconds.Text, out var timeout) || timeout is < 1 or > 600)
            throw new SafeFailure("backend_timeout_invalid");
        var endpoint = BackendSetup.NormalizeEndpoint(Endpoint.Text);
        if (!int.TryParse(MaxTokens.Text, out var tokens) || tokens < 1 || tokens > Policy.OutputTokenLimit(endpoint))
            throw new SafeFailure("backend_tokens_invalid");
        if (!double.TryParse(Temperature.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ||
            !double.IsFinite(temperature) || temperature is < 0 or > 2)
            throw new SafeFailure("backend_temperature_invalid");
        var proxy = (ProxyMode)ProxyModePicker.SelectedIndex;
        var b = new Backend(old?.Id ?? Guid.NewGuid().ToString("N"), BackendName.Text.Trim(), endpoint,
            ModelName.Text.Trim(), old?.CredentialRef ?? "", Vision.IsChecked == true, Loopback.IsChecked == true,
            Streaming.IsChecked == true, timeout, old?.MaxInputBytes ?? 8_388_608, old?.MaxOutputChars ?? 1_000_000,
            temperature, tokens, ProxyMode: proxy, ProxyAddress: proxy == ProxyMode.Custom ? ProxyAddressBox.Text.Trim() : "",
            SendTemperature: SendTemperature.IsChecked == true,
            TokenLimitParameter: UseCompletionTokens.IsChecked == true ? TokenLimitParameter.MaxCompletionTokens : TokenLimitParameter.MaxTokens,
            ThinkingMode: (ThinkingMode)ThinkingModePicker.SelectedIndex);
        Policy.ValidateBackend(b);
        return b;
    }

    private void SaveBackend_Click(object sender, RoutedEventArgs e)
    {
        CancelProbe();
        try
        {
            var draft = BuildBackendSettings();
            // Commit publishes only after the settings write succeeds and rolls back new credentials on failure.
            config = BackendPersistence.Commit(config, draft, ApiKey.Password, credentials, next =>
            {
                if (isolatedWorkspaceTest) return;
                if (configLoadFailed && File.Exists(Configuration.FilePath))
                    File.Copy(Configuration.FilePath, Configuration.FilePath + ".invalid-" + Guid.NewGuid().ToString("N"), false);
                next.Save();
            });
            configLoadFailed = false;
            Populate(draft.Id);
            ConnectionStatus.Text = "模型配置已保存。";
            Status("模型配置已保存");
        }
        catch (Exception ex) { ModelSettingsError(ex); }
    }

    private void ModelSettingsError(Exception ex)
    {
        ConnectionStatus.Text = ConnectionDiagnostics.Describe(ConnectionDiagnostics.CodeFor(ex));
    }

    private void SetProbeBusy(bool busy)
    {
        TestBackendButton.IsEnabled = SaveModelButton.IsEnabled = NewModelButton.IsEnabled = !busy;
        BackendSettings.IsEnabled = EditBackendPicker.IsEnabled = !busy;
        DeleteModelButton.IsEnabled = !busy && EditingBackend is not null;
        SetDefaultModelButton.IsEnabled = !busy && EditingBackend is { } selected && selected.Id != config.SelectedBackendId;
        ConnectionPresetPicker.IsEnabled = !busy;
        CancelProbeButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelProbe()
    {
        if (probeCancellation is null) return;
        var pending = probeCancellation;
        probeCancellation = null;
        pending.Cancel();
        SetProbeBusy(false);
        ConnectionStatus.Text = "连接测试已取消，配置未保存。";
    }

    private void CancelProbe_Click(object sender, RoutedEventArgs e) => CancelProbe();
    private async void TestBackend_Click(object sender, RoutedEventArgs e) => await TestBackendDraftAsync();

    internal async Task TestBackendDraftAsync()
    {
        CancelProbe();
        // Testing a configuration does not require the floating workspace to be in Ready mode.
        // Lock/protection loss still cancels all probes through EmergencyPause.
        if (sessionLocked || !protection.Check() || !reader.Check())
        { ConnectionStatus.Text = "当前会话已锁定或显示保护不可用，无法测试。"; return; }
        Backend draft;
        var secret = ApiKey.Password;
        try
        {
            draft = BuildBackendSettings();
            if (secret.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8192 || secret.Any(char.IsControl))
                    throw new SafeFailure("credential_invalid");
                draft = draft with { CredentialRef = "temporary" };
            }
        }
        catch (Exception ex) { ModelSettingsError(ex); return; }
        using var temporary = new TemporaryCredential(secret);
        using var client = new ChatCompletionsClient(secret.Length > 0 ? temporary : credentials);
        using var cancellation = new CancellationTokenSource();
        probeCancellation = cancellation;
        SetProbeBusy(true);
        ConnectionStatus.Text = $"正在测试当前配置 · {draft.Model} · 超时 {draft.TimeoutSeconds} 秒";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await BackendProbe.RunAsync(client, draft, cancellation.Token);
            if (!ReferenceEquals(probeCancellation, cancellation)) return;
            ConnectionStatus.Text = result.Success && result.ReceivedText
                ? $"连接测试通过 · 已收到完整文本响应 · {elapsed.Elapsed.TotalSeconds:F1} 秒；配置尚未保存。"
                : $"测试失败 · {ConnectionDiagnostics.Describe(result.Success ? "probe_empty_response" : result.Code)}";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(probeCancellation, cancellation)) ModelSettingsError(ex);
        }
        finally
        {
            secret = "";
            if (ReferenceEquals(probeCancellation, cancellation))
            {
                probeCancellation = null;
                SetProbeBusy(false);
            }
        }
    }
}
