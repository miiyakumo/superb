using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Superb.Core;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class ModelSettingsTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Model settings assertion failed"); }
    private static T Control<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static void Click(MainWindow window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
        Check(predicate());
    }
    private sealed class NoRequests : IBackendClient
    {
        public IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, CancellationToken cancellationToken) =>
            throw new Exception("Workspace transport must not be used by settings tests");
    }
    private sealed class ActiveRequest : IBackendClient
    {
        public bool Started { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Completion { get; } = new();
        public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started = true;
            Token = cancellationToken;
            await Completion.Task.WaitAsync(cancellationToken);
            yield return new("retained answer");
            yield return new(Completed: true);
        }
    }

    internal static void Add(List<(string, Action)> tests)
    {
        tests.Add(("New model buttons test the unsaved form and key over HTTP, then save and edit the same configuration", () =>
        {
            using var endpoint = new SyntheticEndpoint();
            var old = new Backend("old", "Old", "https://example.test/v1/chat/completions", "old-model", "");
            var window = new MainWindow(new Configuration { Backends = [old], SelectedBackendId = old.Id }, new NoRequests());
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            string? createdReference = null;
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                Click(window, "NewModelButton");
                Check(Control<ListBox>(window, "EditBackendPicker").SelectedItem is null);
                Check(Control<TextBlock>(window, "ModelEditorHeading").Text.Contains("新建"));
                Control<TextBox>(window, "BackendName").Text = "Synthetic new model";
                Control<TextBox>(window, "Endpoint").Text = endpoint.Url[..^"/chat/completions".Length];
                Control<ComboBox>(window, "ModelName").Text = "new-model";
                Control<PasswordBox>(window, "ApiKey").Password = "synthetic-unsaved-key";
                Control<CheckBox>(window, "Loopback").IsChecked = true;
                Control<CheckBox>(window, "Streaming").IsChecked = false;
                Control<CheckBox>(window, "SendTemperature").IsChecked = false;
                Control<CheckBox>(window, "UseCompletionTokens").IsChecked = true;
                Control<TextBox>(window, "MaxTokens").Text = "1234";
                Click(window, "TestBackendButton");
                Until(() => Control<Button>(window, "TestBackendButton").IsEnabled);
                Check(Control<TextBlock>(window, "ConnectionStatus").Text.Contains("测试通过"));
                Check(Control<ListBox>(window, "EditBackendPicker").Items.Count == 1);
                var request = endpoint.Requests.Single();
                Check(request.GetProperty("model").GetString() == "new-model");
                Check(request.GetProperty("max_completion_tokens").GetInt32() == 1234 && !request.TryGetProperty("temperature", out _));
                Check(request.GetProperty("messages")[0].GetProperty("content").GetString() == BackendProbe.TestPrompt);
                Check(endpoint.Headers.Single().Contains("Authorization: Bearer synthetic-unsaved-key"));
                Check(Control<PasswordBox>(window, "ApiKey").Password == "synthetic-unsaved-key");
                Click(window, "SaveModelButton");
                var saved = window.EditingBackend!;
                createdReference = saved.CredentialRef;
                Check(Control<ListBox>(window, "EditBackendPicker").Items.Count == 2 && saved.Model == "new-model");
                Check(saved.Endpoint == endpoint.Url && saved.CredentialRef.Length > 0);
                Check(Control<PasswordBox>(window, "ApiKey").Password == "");
                // A modified saved model must also test the current edits, while preserving its stored value.
                Control<ComboBox>(window, "ModelName").Text = "edited-model";
                Click(window, "TestBackendButton");
                Until(() => Control<Button>(window, "TestBackendButton").IsEnabled);
                Check(Control<TextBlock>(window, "ConnectionStatus").Text.Contains("测试通过"));
                Check(endpoint.Requests.Last().GetProperty("model").GetString() == "edited-model");
                Check(saved.Model == "new-model" && endpoint.Headers.Last().Contains("Authorization: Bearer synthetic-unsaved-key"));
                Click(window, "SaveModelButton");
                var edited = window.EditingBackend!;
                Check(edited.Id == saved.Id && edited.Model == "edited-model" && edited.CredentialRef == createdReference);
                Check(Control<ListBox>(window, "EditBackendPicker").Items.Count == 2 && endpoint.Error == "");
            }
            finally
            {
                window.ExitApplication();
                if (createdReference is not null) new CredentialStore().Delete(createdReference);
                SynchronizationContext.SetSynchronizationContext(context);
            }
        }));
        tests.Add(("Connection templates and field validation support official, compatible and loopback endpoints", () =>
        {
            var window = new MainWindow(new Configuration(), new NoRequests());
            try
            {
                var picker = Control<ComboBox>(window, "ConnectionPresetPicker");
                picker.SelectedIndex = 2; Click(window, "NewModelButton");
                Check(Control<TextBox>(window, "Endpoint").Text == DeepSeekSetup.Endpoint);
                Check(Control<StackPanel>(window, "ThinkingSettings").Visibility == Visibility.Visible);
                Control<ComboBox>(window, "ThinkingModePicker").SelectedIndex = (int)ThinkingMode.Enabled;
                picker.SelectedIndex = 3; Click(window, "NewModelButton");
                Check(Control<CheckBox>(window, "Loopback").IsChecked == true);
                Check(Control<StackPanel>(window, "ThinkingSettings").Visibility == Visibility.Collapsed);
                Check(Control<ComboBox>(window, "ThinkingModePicker").SelectedIndex == 0);
                Control<ComboBox>(window, "ModelName").Text = "local-model";
                Check(window.BuildBackendSettings().AllowLoopbackHttp);
                Control<TextBox>(window, "TimeoutSeconds").Text = "abc";
                Click(window, "SaveModelButton");
                Check(Control<TextBlock>(window, "ConnectionStatus").Text.Contains("1–600"));
                Check(Control<ListBox>(window, "EditBackendPicker").Items.Count == 0);
                picker.SelectedIndex = 0; Click(window, "NewModelButton");
                Check(Control<TextBox>(window, "Endpoint").Text == "");
                Check(Control<CheckBox>(window, "Loopback").IsChecked == false);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Paused workspace can test a keyless local draft and cancel restores all model controls without saving", () =>
        {
            using var endpoint = new SyntheticEndpoint { ResponseGate = new() };
            var window = new MainWindow(new Configuration(), new NoRequests());
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.EmergencyPause("synthetic pause");
                Control<TextBox>(window, "BackendName").Text = "Keyless local model";
                Control<TextBox>(window, "Endpoint").Text = endpoint.Url;
                Control<ComboBox>(window, "ModelName").Text = "local-model";
                Control<CheckBox>(window, "Loopback").IsChecked = true;
                Control<CheckBox>(window, "Streaming").IsChecked = false;
                Click(window, "TestBackendButton");
                Until(() => endpoint.Requests.Count == 1);
                Check(!Control<Button>(window, "SaveModelButton").IsEnabled);
                Check(Control<Button>(window, "CancelProbeButton").Visibility == Visibility.Visible);
                Check(!endpoint.Headers.Single().Any(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)));
                Click(window, "CancelProbeButton"); Pump();
                Check(Control<Button>(window, "SaveModelButton").IsEnabled && Control<Button>(window, "TestBackendButton").IsEnabled);
                Check(Control<StackPanel>(window, "BackendSettings").IsEnabled);
                Check(Control<TextBlock>(window, "ConnectionStatus").Text.Contains("已取消"));
                Check(Control<ListBox>(window, "EditBackendPicker").Items.Count == 0 && window.WorkspaceCoordinator.Mode == RunState.Paused);
            }
            finally { window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
        }));
        tests.Add(("Settings close hides only settings, keeps the task and supports reopening and explicit exit", () =>
        {
            var model = new Backend("test", "Synthetic", "https://example.test/v1/chat/completions", "model", "");
            var backend = new ActiveRequest();
            var window = new MainWindow(new Configuration { Backends = [model], SelectedBackendId = model.Id }, backend);
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Show(); Pump();
                var reader = window.WorkspaceWindow;
                window.QueueText("retained task");
                var pending = window.RunCommand("send");
                Until(() => backend.Started);
                Control<TextBox>(window, "BackendName").Text = "pending configuration";
                window.Close(); Pump(); // Same WPF Closing path as the title-bar × and Alt+F4.
                Check(!closed && !window.IsVisible && reader.IsVisible && reader.Check());
                Check(window.QueuedText() == "retained task" && !backend.Token.IsCancellationRequested);
                backend.Completion.SetResult(); Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.State == RequestState.Completed);
                Check(((AnswerView)reader.FindName("Answer")).Text == "retained answer");
                window.RunCommand("settings").GetAwaiter().GetResult(); Pump();
                Check(window.IsVisible && Control<TextBox>(window, "BackendName").Text == "pending configuration");
                Click(window, "ExitButton"); Pump();
                Check(closed && !reader.IsVisible);
            }
            finally { window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
        }));
    }
}
