using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

// Opt-in native-input fixture: no user configuration, clipboard, model, or persisted image.
internal static class CaptureSelectionHost
{
    internal static int Run(string[] args)
    {
        var app = new App(isolatedTestHost: true); app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var status = new TextBlock { Text = "Ready", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        var clicks = 0; var releases = 0; var targetClicks = 0; var contexts = 0; var escapeDown = 0; var escapeUp = 0;
        var busy = false; CancellationTokenSource? selection = null;
        var history = new List<object>();
        var directoryIndex = Array.IndexOf(args, "--output-dir");
        var directory = directoryIndex >= 0 ? args[directoryIndex + 1] : "artifacts/capture-input";
        Directory.CreateDirectory(directory);
        var counter = new TextBlock { Text = "Delivered clicks: 0", Margin = new Thickness(8) };
        var start = new Button { Content = "Start two-point selection", Margin = new Thickness(8) };
        var first = new Button { Content = "First corner", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 20, 0, 0) };
        var second = new Button { Content = "Opposite corner", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 30, 20) };
        var area = new Grid { Height = 240, Background = Brushes.SteelBlue, Margin = new Thickness(8) };
        area.Children.Add(first); area.Children.Add(second);
        var input = new TextBox { Margin = new Thickness(8), Height = 32 };
        var chinese = InputLanguageManager.Current.AvailableInputLanguages.Cast<CultureInfo>().FirstOrDefault(c => c.Name == "zh-CN");
        if (chinese is not null) InputLanguageManager.SetInputLanguage(input, chinese);
        InputLanguageManager.SetRestoreInputLanguage(input, true);
        InputMethod.SetPreferredImeState(input, InputMethodState.On);
        var complete = new ComboBox { Margin = new Thickness(8), IsEditable = true, IsTextSearchEnabled = true,
            ItemsSource = new[] { "synthetic-alpha", "synthetic-beta", "synthetic-gamma" } };
        var panel = new StackPanel(); panel.Children.Add(start); panel.Children.Add(status); panel.Children.Add(counter); panel.Children.Add(area);
        panel.Children.Add(new TextBlock { Text = "Synthetic IME input (Chinese layout if installed)", Margin = new Thickness(8, 0, 8, 0) }); panel.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = "WPF autocomplete positive control", Margin = new Thickness(8, 0, 8, 0) }); panel.Children.Add(complete);
        var window = new Window { Title = "Superb capture input test", Width = 640, Height = 610, Left = 80, Top = 80, Topmost = true, Content = panel };
        void Save(string kind)
        {
            counter.Text = $"Down: {clicks} / Up: {releases} / Target: {targetClicks} / Context: {contexts} / Esc: {escapeDown}:{escapeUp}";
            if (history.Count < 2000) history.Add(new { utc = DateTimeOffset.UtcNow, kind, status = status.Text, busy,
                down = clicks, up = releases, targetClicks, contexts, escapeDown, escapeUp,
                inputLength = input.Text.Length, inputIsExpectedChinese = input.Text is "你" or "你好",
                completionLength = complete.Text.Length,
                completionIsExpected = complete.Text is "synthetic-alpha" or "synthetic-beta" or "synthetic-gamma",
                inputLanguage = InputLanguageManager.Current.CurrentInputLanguage.Name });
            File.WriteAllText(Path.Combine(directory, "input-events.json"), JsonSerializer.Serialize(new {
                scope = "Synthetic native WPF controls; production TwoPointSelector; not a browser test. Only synthetic input may be entered.", history
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => { clicks++; Save("mouse-down:" + e.ChangedButton); }), true);
        window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) => { releases++; Save("mouse-up:" + e.ChangedButton); }), true);
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) => { if (e.Key == Key.Escape) escapeDown++; Save("key-down:" + e.Key); }), true);
        window.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler((_, e) => { if (e.Key == Key.Escape) escapeUp++; Save("key-up:" + e.Key); }), true);
        first.Click += (_, _) => { targetClicks++; Save("first-target-click"); };
        second.Click += (_, _) => { targetClicks++; Save("second-target-click"); };
        area.ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "Synthetic context action" } } };
        area.ContextMenuOpening += (_, _) => { contexts++; Save("context-opening"); };
        input.TextChanged += (_, _) => Save("input-changed");
        complete.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Save("completion-changed")));
        TextCompositionManager.AddPreviewTextInputStartHandler(input, (_, _) => Save("composition-start"));
        TextCompositionManager.AddPreviewTextInputUpdateHandler(input, (_, _) => Save("composition-update"));
        TextCompositionManager.AddPreviewTextInputHandler(input, (_, _) => Save("composition-commit"));
        start.Click += async (_, _) =>
        {
            if (busy) return;
            clicks = releases = targetClicks = contexts = escapeDown = escapeUp = 0; busy = true; start.IsEnabled = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); selection = timeout;
            try
            {
                var monitors = CaptureService.Monitors(); status.Text = "Select first corner"; Save("selection-start");
                var result = await TwoPointSelector.SelectAsync(_ => window.Dispatcher.BeginInvoke(() => { status.Text = "Select opposite corner"; Save("first-corner-selected"); }), timeout.Token);
                if (result is null) status.Text = "Cancelled; native hooks released";
                else
                {
                    var target = CaptureService.FromCorners(result.Value.First, result.Value.Second, monitors);
                    var payload = await Task.Run(() => CaptureService.Capture(target));
                    status.Text = $"Captured {payload.Width} x {payload.Height}; native hooks released";
                }
            }
            catch (OperationCanceledException) { status.Text = "Timed out; native hooks released"; }
            catch (Exception ex) { status.Text = "Failed: " + ex.Message; }
            finally { busy = false; start.IsEnabled = true; selection = null; Save("selection-ended"); }
        };
        window.Closed += (_, _) => { selection?.Cancel(); Save("closed"); };
        return app.Run(window);
    }
}
