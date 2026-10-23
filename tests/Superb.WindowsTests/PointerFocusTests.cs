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
        tests.Add(("Native mouse clicks, scene popup, slider, resize and title drags preserve external foreground", () =>
        {
            var input = new TextBox { Text = "Synthetic foreground control" };
            var control = new Window { Title = "Synthetic pointer focus control", Left = 10, Top = 10, Width = 220, Height = 150, Content = input };
            var reader = new ReaderWindow { Left = 260, Top = 30 };
            Native.GetCursorPos(out var oldCursor);
            var activations = 0; var deactivations = 0; var focusLosses = 0;
            var commands = new List<string>();
            control.Deactivated += (_, _) => deactivations++;
            input.LostKeyboardFocus += (_, _) => focusLosses++;
            reader.Activated += (_, _) => activations++;
            reader.Command += action =>
            {
                commands.Add(action);
                if (action == "beginEdit") reader.SetMode(true);
                if (action == "interact") reader.SetMode(!reader.Interactive);
                if (action == "toggle") reader.Hide();
            };
            try
            {
                control.Show(); WorkspaceTests.ChooseWindow(control); input.Focus();
                var foreground = new WindowInteropHelper(control).Handle;
                reader.Show(); reader.Configure(SceneDefaults.All, "general", "Synthetic", "Ctrl+Alt+E");
                var area = SystemParameters.WorkArea;
                reader.ApplyLayout(new PanelLayout(area.Left + area.Width / 4, area.Top + 20, 420, 540, 18, true));
                reader.SetActions(true, false, true, false, false, false); Settle();
                T Named<T>(string name) where T : FrameworkElement => (T)reader.FindName(name);
                void Preserved(string step)
                {
                    Check(Native.GetForegroundWindow() == foreground && Keyboard.FocusedElement == input && activations == 0 && deactivations == 0 && focusLosses == 0,
                        $"{step}: foreground=0x{Native.GetForegroundWindow():X}, expected=0x{foreground:X}, keyboard={Keyboard.FocusedElement?.GetType().Name}, activations={activations}, deactivations={deactivations}, focusLosses={focusLosses}");
                    Check(reader.Check(), step + ": protection failed");
                    Console.WriteLine($"FOCUS PASS {step}: activations=0, deactivations=0, focusLosses=0");
                }

                Click(Named<Button>("AppearanceButton"));
                Check(Named<Border>("AppearancePanel").Visibility == Visibility.Visible, "Native appearance click did not open panel");
                var slider = Named<Slider>("PanelWidth"); var width = reader.Width;
                Drag(Child<Thumb>(slider), 45, 0);
                Check(reader.Width != width && !reader.Interactive, "Width drag must work without editing"); Preserved("slider");
                foreach (var name in new[] { "PanelHeight", "PanelOpacity", "PanelTextSize" })
                {
                    var adjustment = Named<Slider>(name); adjustment.BringIntoView(); Settle();
                    var before = adjustment.Value;
                    var handle = Child<Thumb>(adjustment);
                    var started = 0; var deltas = 0;
                    handle.DragStarted += (_, _) => started++;
                    handle.DragDelta += (_, _) => deltas++;
                    Drag(handle, name == "PanelHeight" ? 15 : -24, 0);
                    Check(adjustment.Value != before && !reader.Interactive,
                        $"{name} did not adjust in reading mode: value={before}->{adjustment.Value}, started={started}, deltas={deltas}, thumb={Center(handle)}");
                    Preserved(name);
                }
                width = reader.Width;
                Drag(Named<Thumb>("ResizeHandle"), 24, 12);
                Check(reader.Width > width && !reader.Interactive, $"Resize drag must work without editing: {width} -> {reader.Width}, interactive={reader.Interactive}"); Preserved("resize");
                var left = reader.Left;
                Drag(Named<TextBlock>("StateText"), 25, 10);
                Check(reader.Left > left, "Header drag did not move the window"); Preserved("title drag");
                Named<Button>("ComfortableButton").BringIntoView(); Settle();
                Click(Named<Button>("ComfortableButton"));
                Check(reader.Width == 800 && reader.Height == 850, "Comfortable preset did not change layout"); Preserved("comfortable preset");
                Click(Named<Button>("AppearanceButton"));
                Named<Button>("ResetAppearanceButton").BringIntoView(); Settle();
                Click(Named<Button>("ResetAppearanceButton"));
                Check(reader.Width == 600 && reader.Height == 740 && Named<AnswerView>("Answer").FontSize == 18,
                    "Reset preset did not restore layout");
                Check(((SolidColorBrush)Application.Current.Resources["Base"]).Color == Color.FromRgb(0x13, 0x14, 0x16)
                    && ((SolidColorBrush)Application.Current.Resources["Accent"]).Color == Color.FromRgb(0x8A, 0xB4, 0xF8),
                    "Reset preset did not restore the ink palette"); Preserved("reset preset");
                Click(Named<Button>("AppearanceButton"));
                Click(Child<ToggleButton>(Named<Expander>("ComposerExpander")));
                Check(Named<Expander>("ComposerExpander").IsExpanded, "Native composer click did not expand input");
                var combo = Named<ComboBox>("WorkspaceScene");
                Click(combo); Check(combo.IsDropDownOpen, "Scene dropdown did not open"); Preserved("scene open");
                var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
                Click(item);
                Check(!combo.IsDropDownOpen && (string)combo.SelectedValue == "coding" && !reader.Interactive, "Mouse scene selection failed");
                Preserved("scene selection");

                Check(reader.SetMode(true), "Enable editing failed");
                var hwnd = new WindowInteropHelper(reader).Handle;
                Check(SendMessage(hwnd, 0x21, foreground, (nint)(0x200 << 16 | 1)) == 3, "Hover activation must stay blocked in edit mode");
                Click(Named<Button>("AppearanceButton")); Preserved("edit mode button");
                Drag(Child<Thumb>(slider), -20, 0); Preserved("edit mode slider");
                Click(Named<Button>("AppearanceButton")); Click(Child<ToggleButton>(Named<Expander>("ComposerExpander")));

                // The scene popup must stay passive in editing mode as well.
                Click(combo); Check(combo.IsDropDownOpen, "Edit-mode scene dropdown did not open"); Preserved("edit mode scene open");
                Click((ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1));
                Check((string)combo.SelectedValue == "algorithm" && !combo.IsDropDownOpen, "Edit-mode scene selection failed");
                Preserved("edit mode scene selection");

                var answer = Named<AnswerView>("Answer");
                reader.Render(string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"Synthetic answer line {i:000} for mouse selection and scrolling.")), "Synthetic answer");
                Settle();
                var scroll = answer.Scroll ?? throw new Exception("Answer scrollbar missing");
                scroll.ScrollToHome(); Settle();
                var bar = Child<ScrollBar>(scroll, s => s.Orientation == Orientation.Vertical && s.IsVisible);
                var oldOffset = scroll.VerticalOffset;
                Drag(Child<Thumb>(bar), 0, 20);
                Check(scroll.VerticalOffset > oldOffset, "Answer scrollbar did not scroll"); Preserved("edit mode answer scrollbar");
                Move(Center(answer.Reader)); Mouse(8); Mouse(16); Preserved("edit mode answer right click");
                Check(reader.SetMode(false), "Reading mode failed");
                scroll.ScrollToHome(); Settle();
                Click(answer.Reader); Preserved("reading body click");
                Move(Center(answer.Reader)); Mouse(0x800, unchecked((uint)-120));
                Check(scroll.VerticalOffset > 0, "Inactive answer wheel did not scroll"); Preserved("reading wheel");
                Click(Child<Button>(answer, b => Equals(b.Content, "回到最新")));
                Check(answer.Following && scroll.VerticalOffset >= scroll.ScrollableHeight - 2, "Follow button did not reach latest"); Preserved("follow latest");

                Check(reader.SetMode(true), "Selection mode failed");
                scroll.ScrollToHome(); Settle();
                Drag(answer.Reader.PointToScreen(new Point(10, 10)), 140, 0);
                Check(Native.GetForegroundWindow() == hwnd && !answer.Reader.Selection.IsEmpty,
                    $"Answer drag selection failed: foreground=0x{Native.GetForegroundWindow():X}, empty={answer.Reader.Selection.IsEmpty}");
                Check(activations > 0 && deactivations > 0, "Text selection positive control did not observe foreground transfer");
                Console.WriteLine("FOCUS EXPECTED answer selection: foreground acquired and non-empty selection verified");

                WorkspaceTests.ChooseWindow(control); input.Focus(); Settle();
                activations = deactivations = focusLosses = 0;
                Click(Named<Button>("InteractionButton")); Preserved("finish editing button");
                Click(Child<ToggleButton>(Named<Expander>("ComposerExpander")));
                reader.SetActions(true, false, true, true, false, true);
                foreach (var (name, action) in new[] { ("SendButton", "send"), ("ClipboardButton", "stageClipboard"),
                    ("CopyButton", "workspaceCopy"), ("RetryButton", "retry"), ("PauseButton", "pause") })
                {
                    var before = commands.Count;
                    Click(Named<Button>(name));
                    Check(commands.Count == before + 1 && commands[^1] == action, name + " did not emit its action");
                    Preserved(name);
                }
                reader.SetDraft(string.Join("\n", Enumerable.Range(1, 30).Select(i => $"Synthetic draft scroll line {i}")));
                Settle();
                var draft = Named<TextBox>("DraftText");
                var draftScroll = Child<ScrollViewer>(draft);
                var draftBar = Child<ScrollBar>(draftScroll, s => s.Orientation == Orientation.Vertical && s.IsVisible);
                Drag(Child<Thumb>(draftBar), 0, 10);
                Check(draftScroll.VerticalOffset > 0 && !reader.Interactive,
                    $"Draft scrollbar should scroll without entering editing: offset={draftScroll.VerticalOffset}, interactive={reader.Interactive}");
                Preserved("draft scrollbar");
                reader.SetDraft("");

                // A genuine input click is the deliberate, documented exception.
                Check(reader.SetMode(false), "Return to reading failed");
                Click(draft);
                Check(reader.Interactive && !draft.IsReadOnly && Native.GetForegroundWindow() == hwnd && Keyboard.FocusedElement == draft,
                    $"Input click did not acquire keyboard focus: interactive={reader.Interactive}, foreground=0x{Native.GetForegroundWindow():X}, keyboard={Keyboard.FocusedElement?.GetType().Name}");
                Check(SendInput(2, [new Input { Type = 1, Keyboard = new KeyboardInput { Scan = 'x', Flags = 4 } },
                    new Input { Type = 1, Keyboard = new KeyboardInput { Scan = 'x', Flags = 6 } }], Marshal.SizeOf<Input>()) == 2, "Typing failed");
                Settle(); Check(reader.Draft == "x", "Keyboard input did not reach the chosen draft");
                Console.WriteLine("FOCUS EXPECTED draft input: first click and synthetic typing verified");
                Check(reader.SetMode(false), "End editing failed"); Settle();
                Check(Native.GetForegroundWindow() == hwnd, "Ending editing switched foreground");
                Click(Named<Button>("HideButton"));
                Check(!reader.IsVisible && Native.GetForegroundWindow() != hwnd, "Explicitly hiding the active reader did not release foreground");
                Console.WriteLine("FOCUS EXPECTED active hide: Windows transferred foreground after the user hide action");
            }
            finally
            {
                Mouse(4); reader.Finish(); control.Close(); SetCursorPos(oldCursor.X, oldCursor.Y);
            }
        }));
        AddCaptureAndSettings(tests);
        AddAppearanceEditing(tests);
        AddPauseResume(tests);
    }

    internal static void AddPauseResume(List<(string, Action)> tests)
    {
        tests.Add(("Native production pause and resume retain external, draft and answer focus while clearing sensitive content", () =>
        {
            var input = new TextBox { Text = "Synthetic external input" };
            var control = new Window { Left = 20, Top = 30, Width = 320, Height = 200, Content = input };
            var main = new MainWindow(new Configuration { ClearOwnedClipboardOnPause = false }, new UnusedBackend());
            var reader = main.WorkspaceWindow;
            Native.GetCursorPos(out var oldCursor);
            try
            {
                new WindowInteropHelper(main).EnsureHandle();
                // The isolated host skips OS listener registration. Model its successful
                // registration for resume, without registering any global test listeners.
                typeof(MainWindow).GetField("sessionListenerReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(main, true);
                reader.ApplyLayout(new PanelLayout(400, 30, 600, 740, 18, true)); reader.Show();
                control.Show(); WorkspaceTests.ChooseWindow(control); input.Focus(); Settle();
                var draft = (TextBox)reader.FindName("DraftText");
                var answer = (AnswerView)reader.FindName("Answer");
                var pause = (Button)reader.FindName("PauseButton");
                foreach (var target in new[] { "external", "draft", "answer" })
                {
                    reader.SetDraft("Synthetic private draft");
                    answer.Render("Synthetic private answer with enough text to select."); Settle();
                    if (target == "draft") Click(draft);
                    if (target == "answer")
                    {
                        Check(reader.SetMode(true), "Cannot enable answer selection");
                        var start = answer.Reader.PointToScreen(new Point(12, 10));
                        Drag(start, 120, 0);
                        Check(!answer.Reader.Selection.IsEmpty, "Answer selection positive control failed");
                    }
                    var focused = Keyboard.FocusedElement;
                    var foreground = Native.GetForegroundWindow();
                    Check(ReferenceEquals(focused, target == "external" ? input : target == "draft" ? draft : answer.Reader),
                        target + ": intended focus positive control failed");
                    var losses = 0; var deactivations = 0;
                    var owner = target == "external" ? control : (Window)reader;
                    var focusedElement = (UIElement)focused;
                    KeyboardFocusChangedEventHandler lost = (_, _) => losses++;
                    EventHandler deactivated = (_, _) => deactivations++;
                    focusedElement.LostKeyboardFocus += lost; owner.Deactivated += deactivated;
                    void Preserved(string step)
                    {
                        Check(Keyboard.FocusedElement == focused && Native.GetForegroundWindow() == foreground && losses == 0 && deactivations == 0,
                            $"{target} {step}: keyboard={Keyboard.FocusedElement?.GetType().Name}, expected={focused.GetType().Name}, losses={losses}, deactivations={deactivations}, foreground=0x{Native.GetForegroundWindow():X}");
                        Check(reader.Check(), target + " " + step + ": protection failed");
                    }
                    try
                    {
                        Click(pause);
                        Check(main.WorkspaceCoordinator.Mode == RunState.Paused && Equals(pause.Content, "恢复") && draft.IsReadOnly &&
                            reader.Draft.Length == 0 && answer.Text.Length == 0 && !reader.Interactive,
                            target + ": pause did not clear/disable the production workspace");
                        Preserved("pause");
                        Click(pause);
                        Check(main.WorkspaceCoordinator.Mode == RunState.Ready && Equals(pause.Content, "暂停") && reader.Draft.Length == 0 && answer.Text.Length == 0,
                            target + ": resume failed or restored cleared content");
                        Preserved("resume");
                        Console.WriteLine($"FOCUS PASS production pause/resume from {target}: zero focus loss; sensitive content cleared");
                    }
                    finally { focusedElement.LostKeyboardFocus -= lost; owner.Deactivated -= deactivated; }
                }
                reader.Hide(); main.Show(); main.Left = 360; main.Top = 20; Settle();
                WorkspaceTests.ChooseWindow(control); input.Focus(); Settle();
                var external = Native.GetForegroundWindow(); var externalLosses = 0; var externalDeactivations = 0;
                input.LostKeyboardFocus += (_, _) => externalLosses++;
                control.Deactivated += (_, _) => externalDeactivations++;
                var settingsPause = Child<Button>(main, b => Equals(b.Content, "暂停"));
                var settingsResume = (Button)main.FindName("ResumeButton");
                Click(settingsPause);
                Check(main.WorkspaceCoordinator.Mode == RunState.Paused, "Settings pause did not execute");
                Check(Native.GetForegroundWindow() == external && Keyboard.FocusedElement == input && externalLosses == 0 && externalDeactivations == 0,
                    $"Settings pause changed focus: keyboard={Keyboard.FocusedElement?.GetType().Name}, losses={externalLosses}, deactivations={externalDeactivations}");
                Click(settingsResume);
                Check(main.WorkspaceCoordinator.Mode == RunState.Ready && Native.GetForegroundWindow() == external && Keyboard.FocusedElement == input &&
                    externalLosses == 0 && externalDeactivations == 0, "Settings resume failed or changed external focus");
                Console.WriteLine("FOCUS PASS settings pause/resume: zero external focus loss; both actions executed");
            }
            finally { Mouse(4); main.Close(); control.Close(); SetCursorPos(oldCursor.X, oldCursor.Y); }
        }));
    }

    internal static void AddAppearanceEditing(List<(string, Action)> tests)
    {
        tests.Add(("Native appearance toggle preserves focused draft, selection, visible caret and continued typing at normal and minimum sizes", () =>
        {
            var reader = new ReaderWindow { Left = 450, Top = 30 };
            Native.GetCursorPos(out var oldCursor);
            try
            {
                reader.Show(); reader.Configure(SceneDefaults.All, "general", "Synthetic", "Ctrl+Alt+E");
                reader.SetActions(true, false, true, false, false, false);
                var draft = (TextBox)reader.FindName("DraftText");
                var composer = (ScrollViewer)reader.FindName("ComposerScroll");
                var expander = (Expander)reader.FindName("ComposerExpander");
                var panel = (Border)reader.FindName("AppearancePanel");
                var button = (Button)reader.FindName("AppearanceButton");
                Settle(); Click(draft);
                Check(draft.IsKeyboardFocused, "Draft positive control did not acquire keyboard focus");
                var handle = new WindowInteropHelper(reader).Handle;
                var focusLosses = 0; var deactivations = 0;
                draft.LostKeyboardFocus += (_, _) => focusLosses++;
                reader.Deactivated += (_, _) => deactivations++;
                foreach (var (width, height) in new[] { (600, 740), (360, 540), (360, 480) })
                {
                    reader.SetDraft(string.Join("\n", Enumerable.Range(0, 25).Select(i => $"Synthetic focused draft line {i:00}")));
                    draft.Select(draft.Text.Length - 10, 5); draft.ScrollToEnd();
                    reader.ApplyLayout(new PanelLayout(450, 30, width, height, 18, true)); Settle();
                    var original = draft.Text; var start = draft.SelectionStart; var length = draft.SelectionLength; var caret = draft.CaretIndex;
                    void Preserved(string phase)
                    {
                        Check(expander.IsExpanded && draft.IsVisible && Keyboard.FocusedElement == draft &&
                            Native.GetForegroundWindow() == handle && focusLosses == 0 && deactivations == 0,
                            $"{width}x{height} {phase}: expanded={expander.IsExpanded}, focus={Keyboard.FocusedElement?.GetType().Name}, losses={focusLosses}, deactivations={deactivations}");
                        var caretRect = draft.GetRectFromCharacterIndex(draft.CaretIndex, true);
                        Check(!caretRect.IsEmpty, "Caret is not laid out");
                        var visibleCaret = draft.TransformToAncestor(composer).TransformBounds(caretRect);
                        var visibleDraft = Rect.Intersect(draft.TransformToAncestor(composer).TransformBounds(new Rect(draft.RenderSize)), new Rect(composer.RenderSize));
                        var windowCaret = draft.TransformToAncestor(reader).TransformBounds(caretRect);
                        Check(visibleDraft.Height >= 30 && visibleCaret.Top >= -1 && visibleCaret.Bottom <= composer.ActualHeight + 1 &&
                            windowCaret.Top >= 0 && windowCaret.Bottom <= reader.ActualHeight - 10,
                            $"{width}x{height} {phase}: composerHeight={composer.ActualHeight}, draftVisibleHeight={visibleDraft.Height}, caret={visibleCaret}, windowCaret={windowCaret}");
                    }
                    Console.WriteLine($"APPEARANCE {width}x{height}: button={Center(button)}, hit={reader.InputHitTest(reader.PointFromScreen(Center(button)))?.GetType().Name}");
                    Click(button); Check(panel.Visibility == Visibility.Visible, $"Appearance did not open at {width}x{height}: actual={panel.Visibility}");
                    Preserved("open");
                    Check(draft.Text == original && draft.SelectionStart == start && draft.SelectionLength == length && draft.CaretIndex == caret,
                        "Opening appearance changed the draft or selection");
                    Check(SendInput(2, [new Input { Type = 1, Keyboard = new KeyboardInput { Scan = 'x', Flags = 4 } },
                        new Input { Type = 1, Keyboard = new KeyboardInput { Scan = 'x', Flags = 6 } }], Marshal.SizeOf<Input>()) == 2, "Continued input failed");
                    Settle();
                    var expected = original.Remove(start, length).Insert(start, "x");
                    Check(draft.Text == expected && draft.CaretIndex == start + 1, "Typing did not replace the original selection");
                    Preserved("type while open");
                    Click(button); Check(panel.Visibility == Visibility.Collapsed, "Appearance did not close");
                    Preserved("close");
                    Check(draft.Text == expected && draft.CaretIndex == start + 1, "Closing appearance changed the draft or caret");
                    Console.WriteLine($"FOCUS PASS focused appearance {width}x{height}: open, continued typing and close; no focus loss");
                }
            }
            finally { Mouse(4); reader.Finish(); SetCursorPos(oldCursor.X, oldCursor.Y); }
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
                var settingsInput = (PasswordBox)main.FindName("QuickApiKey");
                Click(settingsInput);
                Check(Native.GetForegroundWindow() == new WindowInteropHelper(main).Handle && settingsInput.IsKeyboardFocusWithin && deactivations > 0,
                    "Deliberate settings input click did not acquire focus");
                var tabs = (TabControl)main.FindName("MainTabs"); tabs.SelectedItem = main.FindName("AdvancedTab"); Settle();
                var export = Child<Button>(main, b => Equals(b.Content, "导出无凭据配置"));
                export.BringIntoView(); Settle();
                var mainHandle = new WindowInteropHelper(main).Handle;
                var cancelled = Task.Run(async () =>
                {
                    var end = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < end)
                    {
                        var active = Native.GetForegroundWindow();
                        var name = new System.Text.StringBuilder(256); GetClassName(active, name, name.Capacity);
                        if (active != mainHandle && GetAncestor(active, 3) == mainHandle && name.ToString() == "#32770")
                        {
                            // Cancel only this isolated test window's save dialog. No file
                            // is chosen or written, and the user's clipboard stays untouched.
                            Check(SendInput(2, [new Input { Type = 1, Keyboard = new KeyboardInput { Key = 0x1b } },
                                new Input { Type = 1, Keyboard = new KeyboardInput { Key = 0x1b, Flags = 2 } }], Marshal.SizeOf<Input>()) == 2,
                                "Save dialog cancel failed");
                            return true;
                        }
                        await Task.Delay(25);
                    }
                    return false;
                });
                Click(export);
                Until(() => cancelled.IsCompleted, "Save dialog cancel did not complete");
                Check(cancelled.GetAwaiter().GetResult() && main.IsVisible && Native.GetForegroundWindow() == mainHandle,
                    "Save dialog did not acquire focus or return to its owner after cancellation");
                Console.WriteLine("FOCUS EXPECTED export: owned system dialog acquired focus and cancellation returned to settings");
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
                Check(reader.SetMode(false, true), "Pass-through failed");
                Move(Center(input)); Mouse(2); Mouse(4);
                Check(clicks == 1 && releases == 1 && Native.GetForegroundWindow() == foreground && deactivations == 0, "Pass-through did not deliver a complete click");
                Check(reader.SetMode(false), "Exit pass-through failed");
                Click((Button)reader.FindName("HideButton")); Settle();
                Check(reader.IsVisible && Native.GetForegroundWindow() == foreground && deactivations == 0,
                    "Missing return hotkeys must refuse hide without changing foreground");
                reader.Hide(); Settle();
                Check(!reader.IsVisible && deactivations == 0 && Native.GetForegroundWindow() == foreground, "Hiding a background reader changed foreground");
            }
            finally
            {
                main.Close(); control.Close(); SetCursorPos(oldCursor.X, oldCursor.Y);
                SynchronizationContext.SetSynchronizationContext(context);
            }
        };
        tests.Add(("Native settings click, screenshot cancel, consumed corner clicks and pass-through preserve expected foreground", run));
    }
}
