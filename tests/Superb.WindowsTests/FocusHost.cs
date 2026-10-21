using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

// Opt-in, isolated synthetic fixture. Uses the shipped ReaderWindow, Protection and HotkeyManager.
// It never loads user settings, reads the clipboard, contacts a model, or writes credentials.
internal static class FocusHost
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    public static int Run(bool exclusiveReaderKeys = false)
    {
        // WPF requires the same root type as compiled App.xaml; opt out before pumping startup.
        var app = new App(isolatedTestHost: true); app.InitializeComponent();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var reader = new ReaderWindow();
        var area = SystemParameters.WorkArea;
        reader.ApplyLayout(new(Math.Max(20, area.Right - 440), area.Top + 35, 420, Math.Min(620, area.Height - 70), 16, false));
        var hwnd = new WindowInteropHelper(reader).EnsureHandle();
        var control = new Window { Title = "Superb focus positive control", Topmost = true, Width = 360, Height = 220, Left = 25, Top = 40,
            Content = new TextBlock { Text = "Synthetic activation control", Margin = new Thickness(20) } };
        using var hotkeys = new HotkeyManager(hwnd);
        var registrationFailures = hotkeys.Register(new Configuration().Hotkeys, exclusiveReaderKeys);
        var conflictReserved = false;
        var actions = new List<string>();
        var mouseDown = 0; var wheel = 0; var activated = 0; var mouseActivate = 0;
        var text = string.Join("\n", Enumerable.Range(1, 180).Select(n => $"Synthetic test line {n:000} — focus and paging only."));
        void Action(string action)
        {
            if (actions.Count < 1000) actions.Add(action);
            switch (action)
            {
                case "toggle": if (reader.IsVisible) reader.Hide(); else { reader.Show(); reader.Check(); } break;
                case "pageDown": reader.Page(true); break;
                case "pageUp": reader.Page(false); break;
                case "pause": reader.Clear(); break;
                case "settings": control.Show(); control.Activate(); break;
                // The remaining registrations are observable without production side effects.
            }
        }
        reader.Command += Action;
        reader.Activated += (_, _) => activated++;
        reader.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => mouseDown++), true);
        reader.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => wheel++), true);
        HwndSource.FromHwnd(hwnd)!.AddHook((nint h, int msg, nint w, nint l, ref bool handled) =>
        {
            if (msg == 0x21) mouseActivate++;
            if (msg == 0x312 && hotkeys.GetAction((int)w) is { } action) { handled = true; Action(action); }
            return 0;
        });
        object Point(FrameworkElement? element)
        {
            if (element is null || !reader.IsVisible || !element.IsVisible) return new { x = -1d, y = -1d };
            var p = element.PointToScreen(new Point(Math.Min(element.ActualWidth / 2, 160), Math.Min(element.ActualHeight / 2, 90)));
            return new { x = p.X, y = p.Y };
        }
        object State()
        {
            reader.UpdateLayout();
            var view = (FlowDocumentScrollViewer)reader.FindName("Reader");
            var scroll = Find<ScrollViewer>(view);
            var affinityReadable = Native.GetWindowDisplayAffinity(hwnd, out var affinity);
            var compositionResult = Native.DwmIsCompositionEnabled(out var compositionEnabled);
            return new
            {
                hwnd = hwnd.ToInt64(), foreground = GetForegroundWindow().ToInt64(), visible = reader.IsVisible,
                controlVisible = control.IsVisible, exclusiveReaderKeys, conflictReserved,
                startupIntercepted = app.StartupIntercepted, productionWindowCount = app.Windows.OfType<MainWindow>().Count(),
                affinityReadable, affinity, compositionResult, compositionEnabled,
                protection = reader.Check(), styles = Native.GetWindowLongPtr(hwnd, -20).ToInt64(),
                mouseDown, wheel, activated, mouseActivate, actions = actions.ToArray(), registrationFailures,
                registrationErrors = hotkeys.RegistrationErrors, lastWorkerStopped = hotkeys.LastWorkerStopped,
                scrollOffset = scroll?.VerticalOffset ?? 0, scrollableHeight = scroll?.ScrollableHeight ?? 0,
                points = new { body = Point(view), hide = Point(Find<Button>(reader, b => Equals(b.Content, "收起"))),
                    settings = Point(Find<Button>(reader, b => Equals(b.Content, "设置"))), pause = Point(Find<Button>(reader, b => Equals(b.Content, "暂停"))) },
                os = Environment.OSVersion.VersionString, sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                interactive = Environment.UserInteractive, screen = new { width = area.Width, height = area.Height }
            };
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, state = State() }));
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var id = json.RootElement.GetProperty("id").GetInt32();
                    var command = json.RootElement.GetProperty("command").GetString();
                    await dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            switch (command)
                            {
                                case "show": reader.Show(); break;
                                case "hide": reader.Hide(); break;
                                case "render": reader.Render(text, "Synthetic focus test"); break;
                                case "pageDown": reader.Page(true); break;
                                case "pageUp": reader.Page(false); break;
                                case "control": Action("settings"); break;
                                case "closeControl": control.Hide(); break;
                                case "exclusiveOn":
                                    exclusiveReaderKeys = true;
                                    registrationFailures = hotkeys.Register(new Configuration().Hotkeys, true); break;
                                case "exclusiveOff":
                                    exclusiveReaderKeys = false;
                                    registrationFailures = hotkeys.Register(new Configuration().Hotkeys); break;
                                case "conflictOn":
                                    hotkeys.Dispose();
                                    conflictReserved = Native.RegisterHotKey(hwnd, 900, 0x4000, 0x75);
                                    if (!conflictReserved) throw new InvalidOperationException("Test reservation failed");
                                    exclusiveReaderKeys = true;
                                    registrationFailures = hotkeys.Register(new Configuration().Hotkeys, true); break;
                                case "conflictOff":
                                    Native.UnregisterHotKey(hwnd, 900); conflictReserved = false;
                                    registrationFailures = hotkeys.Register(new Configuration().Hotkeys, exclusiveReaderKeys); break;
                                case "state": break;
                                case "quit": dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); break;
                                default: throw new InvalidOperationException("unknown fixture command");
                            }
                            Console.WriteLine(JsonSerializer.Serialize(new { id, state = State() }));
                        }
                        catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { id, error = ex.GetType().Name })); }
                    });
                    if (command == "quit") return;
                }
                catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { error = ex.GetType().Name })); }
            }
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
        Dispatcher.Run();
        if (conflictReserved) Native.UnregisterHotKey(hwnd, 900);
        reader.Finish(); control.Close();
        return 0;
    }
    private static T? Find<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is T match && (predicate is null || predicate(match))) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), predicate) is { } child) return child;
        return null;
    }
}
