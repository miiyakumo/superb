using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

internal static class PackageVerification
{
    internal static void Run()
    {
        using var backend = new ChatCompletionsClient(new CredentialStore());
        var window = new MainWindow(new Configuration { ClearOwnedClipboardOnPause = false }, backend);
        var sample = new Window { Width = 96, Height = 96, WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Brushes.LightBlue,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            new WindowInteropHelper(window.WorkspaceWindow).EnsureHandle();
            if (window.Icon is null || window.WorkspaceWindow.Icon is null)
                throw new InvalidOperationException("Window resources did not load.");
            const string formula = @"\frac{x_1}{\sqrt{2}}";
            var visual = AnswerFormula.TryCreate(formula, formula, true)
                ?? throw new InvalidOperationException("Bundled math font failed to render.");
            sample.Content = visual;
            sample.Show();
            sample.UpdateLayout();
            sample.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Native.DwmFlush();
            var point = sample.PointToScreen(new Point(10, 10));
            var start = new Native.Point { X = (int)point.X, Y = (int)point.Y };
            var end = new Native.Point { X = start.X + 16, Y = start.Y + 16 };
            var target = CaptureService.FromCorners(start, end, CaptureService.Monitors());
            var capture = CaptureService.Capture(target);
            if (capture.Png.IsDefaultOrEmpty || capture.Width != 16 || capture.Height != 16)
                throw new InvalidOperationException("Package screenshot verification failed.");
        }
        finally { sample.Close(); window.ExitApplication(); }
    }
}
