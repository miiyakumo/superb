using System.Threading;
using System.Windows;

namespace Superb.Desktop;

public partial class App : Application
{
    private Mutex? instance;
    private readonly bool isolatedTestHost;
    internal bool StartupIntercepted { get; private set; }
    public App() => Platform.TransientSurfaceProtection.Initialize();
    // Internal test-only entry: preserve the exact XAML root type without launching user workflows.
    internal App(bool isolatedTestHost) : this() => this.isolatedTestHost = isolatedTestHost;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (isolatedTestHost) { StartupIntercepted = true; return; }
        base.OnStartup(e);
        if (e.Args is ["--verify-package"])
        {
            // Exercise the distributed binary without opening a user's session or taking its mutex.
            try { PackageVerification.Run(); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
            return;
        }
        instance = new Mutex(true, "Local\\Superb.Desktop.v1", out var created);
        if (!created) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            if (MainWindow is MainWindow main) main.PauseWithReason("内部操作失败；已暂停", WorkspaceReason.InternalFailure);
            args.Handled = true; // Never show exception bodies or write a crash log.
        };
        var window = new MainWindow(); MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
