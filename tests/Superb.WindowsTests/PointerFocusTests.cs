using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Superb.Core;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

internal static class PointerFocusTests
{
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int count);
    internal static nint MouseActivation(nint hwnd, int mouseMessage) => SendMessage(hwnd, 0x21, 0, (nint)(mouseMessage << 16 | 1));
    private static void Check(bool value, string detail)
    {
        if (!value) { Console.WriteLine("Pointer focus detail: " + detail); throw new Exception(detail); }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Settle(int milliseconds = 100)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { Pump(); Thread.Sleep(5); } while (DateTime.UtcNow < end);
    }
    private static void Mouse(uint flags, uint data = 0)
    {
        Check(SendInput(1, [new Input { Mouse = new MouseInput { Flags = flags, Data = data } }], Marshal.SizeOf<Input>()) == 1, "SendInput mouse failed");
        Settle();
    }
    private static Point Center(FrameworkElement element) => element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
    private static void Move(Point point)
    {
        Check(SetCursorPos((int)point.X, (int)point.Y), "SetCursorPos failed"); Settle();
        Check(Native.GetCursorPos(out var current) && Math.Abs(current.X - point.X) <= 1 && Math.Abs(current.Y - point.Y) <= 1,
            $"Cursor moved during native test: expected={point}, actual={current.X},{current.Y}");
    }
    private static void Click(FrameworkElement element)
    {
        Check(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, "Target is not visible: " + element.Name);
        Move(Center(element));
        Native.GetCursorPos(out var cursor);
        var hit = GetAncestor(WindowFromPoint(cursor), 2);
        var source = (HwndSource)PresentationSource.FromVisual(element);
        Check(hit == source.Handle, $"Click target occluded: name={element.Name}, point={cursor.X},{cursor.Y}, hit=0x{hit:X}, expected=0x{source.Handle:X}");
        Mouse(2); Mouse(4);
    }
    private static void Drag(FrameworkElement element, double x, double y)
    {
        var start = Center(element);
        var window = Window.GetWindow(element);
        Console.WriteLine($"DRAG {element.Name}/{element.GetType().Name}: point={start}, hit={window?.InputHitTest(window.PointFromScreen(start))?.GetType().Name}");
        Drag(Center(element), x, y);
    }
    private static void Drag(Point start, double x, double y)
    {
        Move(start); Mouse(2);
        Console.WriteLine($"DRAG DOWN: capture={System.Windows.Input.Mouse.Captured?.GetType().Name}, pressed={System.Windows.Input.Mouse.LeftButton}");
        try { Move(new Point(start.X + x, start.Y + y)); }
        finally { Mouse(4); }
    }
    private static void Escape()
    {
        Check(SendInput(2, [new Input { Type = 1, Keyboard = new KeyboardInput { Key = 0x1b } },
            new Input { Type = 1, Keyboard = new KeyboardInput { Key = 0x1b, Flags = 2 } }], Marshal.SizeOf<Input>()) == 2, "Escape input failed");
        Settle();
    }
    private static void Until(Func<bool> predicate, string detail)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < end) Settle();
        Check(predicate(), detail);
    }
    private static T Child<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        T? Find(DependencyObject node)
        {
            if (node is T match && (predicate is null || predicate(match))) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (Find(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
            return null;
        }
        return Find(root) ?? throw new Exception("Missing child: " + typeof(T).Name);
    }

    internal static void Add(List<(string, Action)> tests)
    {
        tests.Add(("Native readonly reader resize and scene selection preserve external focus and ordinary pointer", () =>
        {
            var input = new TextBox { Text = "Synthetic external focus" };
            var control = new Window { Left = 10, Top = 10, Width = 240, Height = 150, Content = input };
            var reader = new ReaderWindow();
            Native.GetCursorPos(out var oldCursor);
            try
            {
                control.Show(); WorkspaceTests.ChooseWindow(control); input.Focus();
                var foreground = Native.GetForegroundWindow();
                reader.Show(); reader.ApplyLayout(new PanelLayout(400, 30, 420, 540));
                reader.Configure(SceneDefaults.All, "general", "Synthetic", "Ctrl+Alt+E");
                reader.SetActions(true, false, false, false, false, false); Settle();
                var resize = (Thumb)reader.FindName("ResizeHandle"); var width = reader.Width;
                Check(resize.Cursor == Cursors.Arrow && resize.ForceCursor, "Resize corner changes pointer shape");
                Drag(resize, 25, 15); Settle();
                Check(reader.Width > width && resize.Cursor == Cursors.Arrow && Native.GetForegroundWindow() == foreground && Keyboard.FocusedElement == input, "Resize changed focus or pointer configuration");
                var picker = (ComboBox)reader.FindName("WorkspaceScene"); Click(picker); Settle();
                Click((ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(1)); Settle();
                Check((string)picker.SelectedValue == "algorithm" && Native.GetForegroundWindow() == foreground && Keyboard.FocusedElement == input, "Scene selection changed focus");
                reader.Render(string.Join("\n\n", Enumerable.Range(0, 100).Select(i => "Synthetic answer paragraph " + i)), "已完成"); Settle();
                var answer = (AnswerView)reader.FindName("Answer");
                Check(!answer.Reader.IsSelectionEnabled && !answer.Reader.Focusable && reader.FindName("DraftText") is null, "Removed editing is still available");
                var scroll = answer.Scroll!; scroll.ScrollToHome(); Settle();
                var bar = Child<ScrollBar>(scroll, item => item.IsVisible && item.Orientation == Orientation.Vertical);
                Drag(Child<Thumb>(bar), 0, 20); Settle();
                Check(scroll.VerticalOffset > 0 && Native.GetForegroundWindow() == foreground && reader.Check(), "Answer scrollbar changed focus or stopped working");
            }
            finally { Mouse(4); reader.Finish(); control.Close(); SetCursorPos(oldCursor.X, oldCursor.Y); }
        }));
        AddCaptureAndSettings(tests); AddAppearanceEditing(tests); AddPauseResume(tests);
    }
    internal static void AddPauseResume(List<(string, Action)> tests)
    {
        tests.Add(("Native readonly workspace pause resume clear queue and answer without changing external focus", () =>
        {
            var main = new MainWindow(new Configuration(), new UnusedBackend());
            var input = new TextBox { Text = "Synthetic foreground" };
            var control = new Window { Left = 10, Top = 10, Width = 280, Height = 200, Content = input };
            Native.GetCursorPos(out var oldCursor);
            try
            {
                new WindowInteropHelper(main).EnsureHandle();
                typeof(MainWindow).GetField("sessionListenerReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main, true);
                var reader = main.WorkspaceWindow; reader.Show(); reader.ApplyLayout(new PanelLayout(400, 30, 600, 740));
                main.QueueText("Synthetic private source"); reader.Render("Synthetic private answer", "已完成");
                control.Show(); WorkspaceTests.ChooseWindow(control); input.Focus(); Settle();
                var foreground = Native.GetForegroundWindow(); var pause = (Button)reader.FindName("PauseButton");
                Click(pause); Settle();
                Check(main.WorkspaceCoordinator.Mode == RunState.Paused && main.WorkspaceItems.Count == 0 && ((AnswerView)reader.FindName("Answer")).Text == "", "Pause did not clear material");
                Check(Native.GetForegroundWindow() == foreground && Keyboard.FocusedElement == input, "Pause moved focus");
                Click(pause); Settle();
                Check(main.WorkspaceCoordinator.Mode == RunState.Ready && main.WorkspaceItems.Count == 0 && Native.GetForegroundWindow() == foreground, "Resume restored cleared input or changed focus");
                main.Show(); main.Left = SystemParameters.WorkArea.Right - main.Width; main.Top = 20; Settle();
                WorkspaceTests.ChooseWindow(control); input.Focus(); Settle();
                Click((Button)main.FindName("SettingsPauseButton")); Settle();
                Check(main.WorkspaceCoordinator.Mode == RunState.Paused && Native.GetForegroundWindow() == foreground, "Settings pause moved focus");
                Click((Button)main.FindName("ResumeButton")); Settle();
                Check(main.WorkspaceCoordinator.Mode == RunState.Ready && Native.GetForegroundWindow() == foreground, "Settings resume moved focus");
            }
            finally { Mouse(4); main.ExitApplication(); control.Close(); SetCursorPos(oldCursor.X, oldCursor.Y); }
        }));
    }

    internal static void AddAppearanceEditing(List<(string, Action)> tests)
    {
        tests.Add(("Native settings appearance sliders preview the workspace without clearing its input or entering reader editing", () =>
        {
            var window = new MainWindow(new Configuration(), new UnusedBackend());
            Native.GetCursorPos(out var oldCursor);
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var reader = window.WorkspaceWindow; reader.Show();
                var area = SystemParameters.WorkArea;
                reader.ApplyLayout(new PanelLayout(area.Right - 360, area.Top + 30, 360, 540, 18, true));
                window.QueueText("retained native material"); reader.Render("retained native answer", "已完成");
                window.Show(); window.Left = area.Left + 10; window.Top = area.Top + 10;
                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("AppearanceTab");
                Settle();
                var slider = (Slider)window.FindName("PanelOpacity"); slider.BringIntoView(); Settle();
                var before = reader.Layout().BackgroundOpacity;
                Drag(Child<Thumb>(slider), -35, 0); Settle();
                Check(reader.Layout().BackgroundOpacity != before, "Settings slider did not preview the reader");
                Check(window.QueuedText() == "retained native material" && ((AnswerView)reader.FindName("Answer")).Text == "retained native answer" && reader.Check(), "Appearance changed task data or protection");
                Click((Button)window.FindName("SettingsCloseButton")); Settle();
                Check(!window.IsVisible && reader.IsVisible && window.QueuedText() == "retained native material", "Close button did not return to the retained workspace");
            }
            finally { Mouse(4); window.ExitApplication(); SetCursorPos(oldCursor.X, oldCursor.Y); }
        }));
    }

    private sealed class UnusedBackend : IBackendClient
    {
        public IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, CancellationToken cancellationToken) => throw new Exception("Pointer test must not call a backend");
    }

    private static void AddCaptureAndSettings(List<(string, Action)> tests)
    {
        Action run = () =>
        {
            var input = new TextBox { Text = "Synthetic target" };
            var control = new Window { Title = "Synthetic native settings target", Left = 20, Top = 30, Width = 350, Height = 240, Content = input };
            var main = new MainWindow(new Configuration(), new UnusedBackend());
            var reader = main.WorkspaceWindow;
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
            Native.GetCursorPos(out var oldCursor);
            var clicks = 0; var releases = 0; var deactivations = 0; var escaped = 0;
            control.AddHandler(System.Windows.Input.Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => clicks++), true);
            control.AddHandler(System.Windows.Input.Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => releases++), true);
            control.Deactivated += (_, _) => deactivations++;
            input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) escaped++; };
            input.PreviewKeyUp += (_, e) => { if (e.Key == Key.Escape) escaped++; };
            try
            {
                new WindowInteropHelper(main).EnsureHandle();
                reader.Show(); reader.ApplyLayout(new PanelLayout(500, 30, 600, 740, 18, true));
                control.Show(); WorkspaceTests.ChooseWindow(control); input.Focus(); Settle();
                var foreground = new WindowInteropHelper(control).Handle;
                Click((Button)reader.FindName("SettingsButton"));
                Check(main.IsVisible && Native.GetForegroundWindow() == foreground && deactivations == 0, "Settings button activated the settings window");
                main.Left = SystemParameters.WorkArea.Right - main.Width - 20; main.Top = 30; Settle();
                var settingsInput = (PasswordBox)main.FindName("ApiKey");
                Click(settingsInput);
                Check(Native.GetForegroundWindow() == new WindowInteropHelper(main).Handle && settingsInput.IsKeyboardFocusWithin && deactivations > 0,
                    "Deliberate settings input click did not acquire focus");
                var tabs = (TabControl)main.FindName("MainTabs"); tabs.SelectedItem = main.FindName("AdvancedTab"); Settle();
                WorkspaceTests.ChooseWindow(control); input.Focus(); Settle(); deactivations = 0;
                main.Hide();
                Click((Button)reader.FindName("CaptureButton"));
                Until(() => reader.ClickThrough, "Screenshot button did not start selection"); Settle();
                Check(Native.GetForegroundWindow() == foreground && deactivations == 0, "Screenshot selection changed foreground");
                Move(Center(input)); Mouse(8); Mouse(16); // right down / up cancels, and must be consumed
                Until(() => !reader.ClickThrough, "Screenshot right-click cancellation did not finish");
                Check(clicks == 0 && releases == 0 && Native.GetForegroundWindow() == foreground && deactivations == 0,
                    $"Cancellation leaked mouse input or focus: clicks={clicks}, releases={releases}, losses={deactivations}");
                Click((Button)reader.FindName("CaptureButton"));
                Until(() => reader.ClickThrough, "Second screenshot selection did not begin"); Settle();
                Escape();
                Until(() => !reader.ClickThrough, "Escape did not cancel screenshot selection");
                Check(escaped == 0 && deactivations == 0 && Native.GetForegroundWindow() == foreground, "Screenshot Escape leaked or changed foreground");
                Console.WriteLine("FOCUS PASS screenshot right-click and Escape cancellation: no forwarded input or focus loss");

                // Exercise the production selection hook through successful corner input,
                // without capturing or saving any desktop pixels.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var progress = 0;
                var selection = TwoPointSelector.SelectAsync(_ => Interlocked.Increment(ref progress), timeout.Token);
                Settle();
                var first = input.PointToScreen(new Point(20, 20));
                var second = input.PointToScreen(new Point(150, 90));
                Move(first); Mouse(2); Mouse(4);
                Until(() => Volatile.Read(ref progress) == 1, "First corner was not recorded");
                Move(second); Mouse(2); Mouse(4);
                Until(() => selection.IsCompleted, "Second corner was not recorded");
                var result = selection.GetAwaiter().GetResult();
                Check(result is not null && result.Value.First.X == (int)first.X && result.Value.Second.X == (int)second.X,
                    "Production corner selector returned different coordinates");
                Check(clicks == 0 && releases == 0 && deactivations == 0 && Native.GetForegroundWindow() == foreground, "Corner selection leaked input or focus");
                Console.WriteLine("FOCUS PASS corner selection: both click pairs consumed, foreground retained");

                // Put the reader over our synthetic input. Pass-through must deliver one
                // complete click, then ordinary reading must intercept it again.
                reader.Left = control.Left; reader.Top = control.Top; Settle();
                Check(reader.SetClickThrough(true), "Pass-through failed");
                Move(Center(input)); Mouse(2); Mouse(4);
                Check(clicks == 1 && releases == 1 && Native.GetForegroundWindow() == foreground && deactivations == 0, "Pass-through did not deliver a complete click");
                Check(reader.SetClickThrough(false), "Exit pass-through failed");
                Click((Button)reader.FindName("HideButton")); Settle();
                Check(reader.IsVisible && Native.GetForegroundWindow() == foreground && deactivations == 0,
                    "Missing return hotkeys must refuse hide without changing foreground");
                reader.Hide(); Settle();
                Check(!reader.IsVisible && deactivations == 0 && Native.GetForegroundWindow() == foreground, "Hiding a background reader changed foreground");
            }
            finally
            {
                main.ExitApplication(); control.Close(); SetCursorPos(oldCursor.X, oldCursor.Y);
                SynchronizationContext.SetSynchronizationContext(context);
            }
        };
        tests.Add(("Native settings click, screenshot cancel, consumed corner clicks and pass-through preserve expected foreground", run));
    }
}
