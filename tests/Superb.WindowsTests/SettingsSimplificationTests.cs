using System.Collections.Immutable;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using System.IO;
using Superb.Core;
using Superb.Desktop;

namespace Superb.WindowsTests;

internal static class SettingsSimplificationTests
{
    private sealed class UnusedBackend : IBackendClient
    {
        public IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, CancellationToken cancellationToken) =>
            throw new Exception("Settings must not call a model");
    }
    private static void Check(bool value) { if (!value) throw new Exception("Settings simplification assertion failed"); }
    private static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static void Click(MainWindow window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(1); }
        Check(predicate());
    }
    private sealed class GatedBackend : IBackendClient
    {
        public TaskCompletionSource Complete { get; } = new();
        public int Calls { get; private set; }
        public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            yield return new("retained partial answer");
            await Complete.Task.WaitAsync(cancellationToken);
            yield return new(Completed: true);
        }
    }
    private static void Render(MainWindow window, string name, string[] args)
    {
        var index = Array.IndexOf(args, "--render-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        window.Show(); Pump();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        Directory.CreateDirectory(args[index + 1]);
        using var file = File.Create(Path.Combine(args[index + 1], name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
    }

    internal static void Add(List<(string, Action)> tests, string[] args)
    {
        tests.Add(("Settings tabs share a compact header with workspace and pause actions at minimum and default widths", () =>
        {
            var window = new MainWindow(new Configuration(), new UnusedBackend());
            try
            {
                new WindowInteropHelper(window).EnsureHandle(); window.Show();
                foreach (var width in new[] { 800d, 900d })
                {
                    window.Width = width; Pump();
                    var actions = Control<StackPanel>(window, "SettingsHeaderActions");
                    var actionStart = actions.TranslatePoint(new Point(), window);
                    var tabs = Control<TabControl>(window, "MainTabs");
                    foreach (TabItem tab in tabs.Items)
                    {
                        var tabStart = tab.TranslatePoint(new Point(), window);
                        Check(tabStart.X + tab.ActualWidth <= actionStart.X && Math.Abs(tabStart.Y - actionStart.Y) < 8);
                        tabs.SelectedItem = tab; Pump();
                        Check(tab.IsSelected && actions.IsVisible);
                    }
                    var pause = Control<Button>(window, "SettingsPauseButton");
                    Check(pause.IsVisible && pause.TranslatePoint(new Point(pause.ActualWidth, 0), window).X <= window.ActualWidth - 20);
                    Check(Control<Border>(window, "SettingsStatusCard").Visibility == Visibility.Collapsed);
                }
                Render(window, "settings-compact-header", args);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Settings status shows validation and protection failures but hides successful saves and deliberate pause", () =>
        {
            var window = new MainWindow(new Configuration(), new UnusedBackend());
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var card = Control<Border>(window, "SettingsStatusCard");
                Check(card.Visibility == Visibility.Collapsed);
                Control<TabControl>(window, "MainTabs").SelectedItem = window.FindName("ScenesTab");
                var name = Control<TextBox>(window, "SceneName"); var original = name.Text;
                name.Text = ""; Click(window, "SaveSceneButton");
                Check(card.Visibility == Visibility.Visible && Control<TextBlock>(window, "ErrorCode").Text == "scene_invalid");
                Render(window, "settings-validation-error", args);
                name.Text = original; Click(window, "SaveSceneButton");
                Check(card.Visibility == Visibility.Collapsed && Control<Expander>(window, "ErrorDetails").Visibility == Visibility.Collapsed);
                Click(window, "SettingsPauseButton");
                Check(card.Visibility == Visibility.Collapsed && Control<Button>(window, "ResumeButton").Visibility == Visibility.Visible);
                Check(Control<Button>(window, "SettingsPauseButton").Visibility == Visibility.Collapsed);
                window.EmergencyPause("显示配置变化；已暂停，请恢复后重新截图", exceptional: true);
                Check(card.Visibility == Visibility.Visible);
                window.EmergencyPause("Synthetic protection failure", RunState.ProtectionUnavailable);
                Check(card.Visibility == Visibility.Visible);
                window.RunCommand("newTask").GetAwaiter().GetResult();
                Check(card.Visibility == Visibility.Visible && Control<TextBlock>(window, "StatusLabel").Text.Contains("保护不可用"));
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Workspace panels and primary button backgrounds follow opacity and theme without fading text or settings", () =>
        {
            var window = new MainWindow(new Configuration(), new UnusedBackend());
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var reader = window.WorkspaceWindow; reader.Show();
                window.RunCommand("scene:coding").GetAwaiter().GetResult();
                window.QueueText("Synthetic source material");
                reader.Render("**Synthetic directive**\n\n`code` and [reference](https://example.test)", "已完成");
                foreach (var theme in new[] { "ThemeInk", "ThemePaper" })
                {
                    Click(window, theme);
                    foreach (var opacity in new[] { .25, .6, 1.0 })
                    {
                        Control<Slider>(window, "PanelOpacity").Value = opacity; Pump();
                        var alpha = (byte)Math.Round(opacity * 255);
                        var expectedSurface = ((SolidColorBrush)Application.Current.Resources["Surface"]).Color;
                        foreach (var name in new[] { "Attachment", "AnswerSurface" })
                        {
                            var panel = (Border)reader.FindName(name);
                            Check(((SolidColorBrush)panel.Background).Color == Color.FromArgb(alpha, expectedSurface.R, expectedSurface.G, expectedSurface.B));
                            Check(panel.Opacity == 1);
                        }
                        foreach (var name in new[] { "SendButton", "ScreenSendButton" })
                        {
                            var button = (Button)reader.FindName(name); button.ApplyTemplate();
                            var background = (Border)button.Template.FindName("Bd", button);
                            var accent = ((SolidColorBrush)Application.Current.Resources["Accent"]).Color;
                            Check(((SolidColorBrush)background.Background).Color == Color.FromArgb(alpha, accent.R, accent.G, accent.B));
                            Check(((SolidColorBrush)button.Foreground).Color.A == 255 && button.Opacity == 1);
                            Check(((SolidColorBrush)button.FindResource("Surface")).Color.A == alpha);
                        }
                        Check(((SolidColorBrush)reader.FindResource("Accent")).Color.A == 255);
                        Check(((SolidColorBrush)Application.Current.Resources["Surface"]).Color.A == 255);
                        Check(((SolidColorBrush)Application.Current.Resources["Accent"]).Color.A == 255 && window.Opacity == 1 && reader.Opacity == 1);
                        Check(((AnswerView)reader.FindName("Answer")).Reader.Document.Foreground.Opacity == 1 && reader.Check());
                        var index = Array.IndexOf(args, "--render-dir");
                        if (index >= 0 && index + 1 < args.Length && opacity != .6)
                        {
                            Directory.CreateDirectory(args[index + 1]);
                            var bitmap = new RenderTargetBitmap((int)reader.ActualWidth, (int)reader.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(reader); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(args[index + 1], $"workspace-opacity-{theme}-{opacity * 100:0}.png")); encoder.Save(file);
                        }
                    }
                }
            }
            finally { Click(window, "ResetAppearanceButton"); window.ExitApplication(); }
        }));
        tests.Add(("Settings appearance previews preserve draft answer protection and exact saved layout; resizing synchronizes the editor", () =>
        {
            var window = new MainWindow(new Configuration { Panel = new(40, 40, 600, 650, 24, false, .8, Theme.MistId) }, new UnusedBackend());
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                var reader = window.WorkspaceWindow; reader.Show(); Pump();
                window.QueueText("retained draft"); reader.Render("retained answer", "已完成");
                Check(reader.FindName("DraftText") is null && reader.FindName("InteractionButton") is null);
                var answer = (AnswerView)reader.FindName("Answer");
                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("AppearanceTab");
                Check(reader.FindName("AppearanceButton") is null && reader.FindName("AppearancePanel") is null);
                Check(Control<Slider>(window, "PanelOpacity").Value == .8 && Control<Slider>(window, "PanelTextSize").Value == 24);
                Control<Slider>(window, "PanelOpacity").Value = .55;
                Control<Slider>(window, "PanelTextSize").Value = 30;
                Control<Slider>(window, "PanelWidth").Value = 680;
                Check(reader.Layout().BackgroundOpacity == .55 && answer.FontSize == 30 && reader.Opacity == 1);
                Check(reader.FontSize == 14 && answer.Reader.Document.Foreground.Opacity == 1);
                Check(((SolidColorBrush)((Border)reader.FindName("WindowSurface")).Background).Color.A == (byte)Math.Round(.55 * 255));
                var before = reader.Width;
                ((Thumb)reader.FindName("ResizeHandle")).RaiseEvent(new DragDeltaEventArgs(20, 10) { RoutedEvent = Thumb.DragDeltaEvent }); Pump();
                Check(reader.Width > before && Control<Slider>(window, "PanelWidth").Value == reader.Width);
                Click(window, "ThemePaper");
                Check(reader.Layout().Theme == Theme.PaperId && ((SolidColorBrush)reader.Foreground).Color == Color.FromRgb(0x1A, 0x1C, 0x1F));
                Control<TextBox>(window, "CustomBaseBox").Text = "#101216";
                Control<TextBox>(window, "CustomInkBox").Text = "#F4F6F8";
                Control<TextBox>(window, "CustomAccentBox").Text = "#7DCEA0";
                Click(window, "ApplyCustomButton");
                var saved = reader.Layout();
                Check(saved.CustomBase == "#101216" && saved.Accent == "#7DCEA0" && window.BuildClipboardPreferences().Panel == saved);
                var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(window.BuildClipboardPreferences(), Configuration.Json), Configuration.Json)!;
                restored.Validate(); Check(restored.Panel == saved);
                Control<TextBox>(window, "CustomInkBox").Text = "#101216"; Click(window, "ApplyCustomButton");
                Check(Control<TextBlock>(window, "ThemeError").Text.Length > 0 && reader.Layout() == saved);
                Click(window, "ComfortableButton");
                Check(!((Expander)reader.FindName("ComposerExpander")).IsExpanded && window.QueuedText() == "retained draft");
                Click(window, "ResetAppearanceButton");
                Check(reader.Layout().FontSize == 18 && reader.Layout().BackgroundOpacity == 1 && reader.Layout().Theme == Theme.InkId);
                Check(window.QueuedText() == "retained draft" && answer.Text == "retained answer" && reader.Check());
                Render(window, "settings-appearance", args);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("One connection list separates editing saving and default selection without saving pending fields", () =>
        {
            var first = new Backend("first", "First", "https://example.test/chat/completions", "model-a", "");
            var second = first with { Id = "second", Name = "Second", Model = "model-b" };
            var window = new MainWindow(new Configuration { Backends = [first, second], SelectedBackendId = first.Id }, new UnusedBackend());
            try
            {
                var list = Control<ListBox>(window, "EditBackendPicker");
                Check(window.FindName("BackendPicker") is null && window.FindName("ExclusiveReaderKeys") is null);
                list.SelectedIndex = 1;
                Check(window.EditingBackend == second && window.BuildClipboardPreferences().SelectedBackendId == first.Id);
                Control<TextBox>(window, "BackendName").Text = "Saved second"; Click(window, "SaveModelButton");
                Check(window.EditingBackend?.Name == "Saved second" && window.BuildClipboardPreferences().SelectedBackendId == first.Id);
                Control<TextBox>(window, "BackendName").Text = "Unsaved name"; Click(window, "SetDefaultModelButton");
                Check(window.BuildClipboardPreferences().SelectedBackendId == second.Id && Control<TextBox>(window, "BackendName").Text == "Unsaved name");
                Check(window.BuildClipboardPreferences().Backends.Single(b => b.Id == second.Id).Name == "Saved second");
                Check(!Control<Button>(window, "SetDefaultModelButton").IsEnabled);
                var defaults = list.Items.Cast<object>().Where(row => (bool)row.GetType().GetProperty("IsDefault")!.GetValue(row)!);
                Check(defaults.Single().GetType().GetProperty("Id")!.GetValue(defaults.Single()) as string == second.Id);
                Check(Control<Button>(window, "SettingsCloseButton").ToolTip.ToString()!.Contains("工作区继续运行"));
                Check(!Control<Expander>(window, "DiagnosticsSettings").IsExpanded);
                Render(window, "settings-model-list", args);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Schema 5 preserves effective per-row exclusive shortcuts without a master switch or screenshot exception", () =>
        {
            var old = new Configuration { SchemaVersion = 4 };
            old = old with { Hotkeys = old.Hotkeys.Select(k => k.Action == "clipboard" ? k with { Mode = HotkeyMode.Exclusive, Gesture = "F16" } : k).ToImmutableArray(),
                Scenes = old.Scenes.Select(s => s.Id == "coding" ? s with { Trigger = s.Trigger with { Mode = HotkeyMode.Exclusive, Gesture = "F17" } } : s).ToImmutableArray() };
            var migrated = old.Migrate(); migrated.Validate();
            Check(!migrated.Hotkeys.Single(k => k.Action == "clipboard").Enabled && !migrated.Scenes[0].Trigger.Enabled);
            Check(migrated.Hotkeys.Single(k => k.Action == "captureScreenSend").Enabled);
            Check(migrated.Migrate() == migrated);
            var active = (old with { ExclusiveHotkeysEnabled = true }).Migrate(); active.Validate();
            Check(active.Hotkeys.Single(k => k.Action == "clipboard").Enabled && active.Scenes[0].Trigger.Enabled);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(active, Configuration.Json));
            Check(!json.RootElement.TryGetProperty("ExclusiveHotkeysEnabled", out _) && !json.RootElement.TryGetProperty("ExclusiveReaderKeys", out _));
            Check(ShortcutLabel.Describe(new("clipboard", "F16", HotkeyMode.Exclusive), true).Active);
        }));
        tests.Add(("Hotkey row context menu configures and saves exclusive mode without a global switch", () =>
        {
            var window = new MainWindow(new Configuration(), new UnusedBackend());
            try
            {
                window.Show();
                Control<TabControl>(window, "MainTabs").SelectedItem = window.FindName("ShortcutsTab"); Pump();
                var rows = Control<ItemsControl>(window, "HotkeyRows");
                var row = ((IEnumerable<HotkeyEditorRow>)rows.ItemsSource).First(r => r.Action == "clipboard");
                T? Find<T>(DependencyObject node) where T : DependencyObject
                {
                    if (node is T match) return match;
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                        if (Find<T>(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
                    return null;
                }
                var presenter = (DependencyObject)rows.ItemContainerGenerator.ContainerFromIndex(0);
                var editor = Find<TextBox>(presenter)!;
                var menu = editor.ContextMenu!; menu.PlacementTarget = editor; menu.IsOpen = true; Pump();
                row.Gesture = "F23";
                var mode = (MenuItem)menu.Items[0]; mode.IsChecked = true; Pump();
                Check(row.IsExclusive && row.Mode == HotkeyMode.Exclusive && editor.Text == "F23");
                menu.IsOpen = false; Click(window, "SaveHotkeysButton");
                Check(window.BuildClipboardPreferences().Hotkeys.Single(k => k.Action == "clipboard") is { Enabled: true, Mode: HotkeyMode.Exclusive, Gesture: "F23" });
                Render(window, "settings-shortcuts", args);
            }
            finally { window.ExitApplication(); }
        }));
        tests.Add(("Workspace shows only relevant stop copy and retry actions across generation stop completion and a new task", () =>
        {
            var backend = new GatedBackend();
            var connection = new Backend("test", "Test", "https://example.test/chat/completions", "model", "");
            var window = new MainWindow(new Configuration { Backends = [connection], SelectedBackendId = connection.Id }, backend);
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                new WindowInteropHelper(window).EnsureHandle(); var reader = window.WorkspaceWindow; reader.Show();
                var stop = (Button)reader.FindName("StopButton"); var copy = (Button)reader.FindName("CopyButton"); var retry = (Button)reader.FindName("RetryButton");
                Check(stop.Visibility == Visibility.Collapsed && copy.Visibility == Visibility.Collapsed && retry.Visibility == Visibility.Collapsed);
                window.QueueText("first question"); var pending = window.RunCommand("send"); Until(() => backend.Calls == 1);
                Check(stop.Visibility == Visibility.Visible && copy.Visibility == Visibility.Collapsed && retry.Visibility == Visibility.Collapsed);
                Check(Control<Border>(window, "SettingsStatusCard").Visibility == Visibility.Collapsed);
                Control<Slider>(window, "PanelTextSize").Value = 23;
                Check(window.WorkspaceCoordinator.Current!.Request.Content.Text == "first question" && backend.Calls == 1);
                window.RunCommand("cancel").GetAwaiter().GetResult(); Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(stop.Visibility == Visibility.Collapsed && copy.Visibility == Visibility.Visible && retry.Visibility == Visibility.Visible);
                Check(Control<Border>(window, "SettingsStatusCard").Visibility == Visibility.Collapsed);
                window.RunCommand("newTask").GetAwaiter().GetResult();
                Check(copy.Visibility == Visibility.Collapsed && retry.Visibility == Visibility.Collapsed && window.QueuedText() == "");
                window.QueueText("second question"); pending = window.RunCommand("send"); Until(() => backend.Calls == 2);
                backend.Complete.SetResult(); Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(window.WorkspaceCoordinator.State == RequestState.Completed && stop.Visibility == Visibility.Collapsed && copy.Visibility == Visibility.Visible && retry.Visibility == Visibility.Visible);
                Check(Control<Border>(window, "SettingsStatusCard").Visibility == Visibility.Collapsed);
            }
            finally { window.ExitApplication(); SynchronizationContext.SetSynchronizationContext(context); }
        }));
        tests.Add(("Former edit shortcut migrates into full pass-through recovery while preserving custom key mode and disabled state", () =>
        {
            foreach (var enabled in new[] { true, false })
            {
                var key = new Hotkey("interact", "F23", HotkeyMode.Exclusive, enabled);
                var old = new Configuration { SchemaVersion = 5,
                    Hotkeys = HotkeyDefaults.All.Where(k => k.Action != "through").Append(key).ToImmutableArray() };
                var migrated = old.Migrate(); migrated.Validate();
                Check(migrated.SchemaVersion == 6 && migrated.Hotkeys.Single(k => k.Action == "through") == key with { Action = "through" });
                Check(!migrated.Hotkeys.Any(k => k.Action == "interact") && migrated.Migrate() == migrated);
                var saved = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(migrated, Configuration.Json), Configuration.Json)!.Migrate();
                saved.Validate(); Check(saved.Hotkeys.SequenceEqual(migrated.Hotkeys));
            }
        }));
        tests.Add(("Full pass-through recovery exits without enabling editing and cannot be entered without a registered escape key", () =>
        {
            var window = new MainWindow(new Configuration(), new UnusedBackend());
            try
            {
                new WindowInteropHelper(window).EnsureHandle(); var reader = window.WorkspaceWindow; reader.Show(); Pump();
                window.QueueText("retained source");
                var origin = Superb.Desktop.Platform.Native.GetForegroundWindow();
                Check(reader.SetClickThrough(true) && reader.InputRegions().Count == 0);
                window.RunCommand("through").GetAwaiter().GetResult();
                Check(!reader.ClickThrough && reader.InputRegions().Count > 0 && window.QueuedText() == "retained source");
                Check(reader.FindName("DraftText") is null && reader.FindName("InteractionButton") is null && reader.Check());
                window.RunCommand("through").GetAwaiter().GetResult();
                Check(!reader.ClickThrough && Superb.Desktop.Platform.Native.GetForegroundWindow() == origin);
            }
            finally { window.ExitApplication(); }
        }));
    }
}
