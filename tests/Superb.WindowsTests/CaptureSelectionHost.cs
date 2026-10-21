using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

// Opt-in native-input fixture: no user configuration, clipboard, model, or persisted image.
internal static class CaptureSelectionHost
{
    internal static int Run()
    {
        var app = new App(isolatedTestHost: true); app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var status = new TextBlock { Text = "Ready", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        var clicks = 0; var busy = false; CancellationTokenSource? selection = null;
        var counter = new TextBlock { Text = "Delivered clicks: 0", Margin = new Thickness(8) };
        var start = new Button { Content = "Start two-point selection", Margin = new Thickness(8) };
        var first = new Button { Content = "First corner", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 20, 0, 0) };
        var second = new Button { Content = "Opposite corner", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 30, 20) };
        var area = new Grid { Height = 240, Background = Brushes.SteelBlue, Margin = new Thickness(8) };
        area.Children.Add(first); area.Children.Add(second);
        var panel = new StackPanel(); panel.Children.Add(start); panel.Children.Add(status); panel.Children.Add(counter); panel.Children.Add(area);
        var window = new Window { Title = "Superb capture input test", Width = 640, Height = 480, Left = 80, Top = 80, Topmost = true, Content = panel };
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => counter.Text = $"Delivered clicks: {++clicks}"), true);
        start.Click += async (_, _) =>
        {
            if (busy) return;
            clicks = 0; counter.Text = "Delivered clicks: 0"; busy = true; start.IsEnabled = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); selection = timeout;
            try
            {
                var monitors = CaptureService.Monitors(); status.Text = "Select first corner";
                var result = await TwoPointSelector.SelectAsync(_ => window.Dispatcher.BeginInvoke(() => status.Text = "Select opposite corner"), timeout.Token);
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
            finally { busy = false; start.IsEnabled = true; selection = null; }
        };
        window.Closed += (_, _) => selection?.Cancel();
        return app.Run(window);
    }
}
