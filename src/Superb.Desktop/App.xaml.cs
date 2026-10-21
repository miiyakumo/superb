using System.Threading;
using System.Windows;

namespace Superb.Desktop;

public partial class App : Application
{
    private Mutex? instance;
    private readonly bool isolatedTestHost;
    internal bool StartupIntercepted { get; private set; }
    public App() { }
    // Internal test-only entry: preserve the exact XAML root type without launching user workflows.
    internal App(bool isolatedTestHost) => this.isolatedTestHost = isolatedTestHost;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (isolatedTestHost) { StartupIntercepted = true; return; }
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\Superb.Desktop.v1", out var created);
        if (!created) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            if (MainWindow is MainWindow main) main.EmergencyPause("内部操作失败；已暂停");
            args.Handled = true; // Never show exception bodies or write a crash log.
        };
        var window = new MainWindow(); MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
