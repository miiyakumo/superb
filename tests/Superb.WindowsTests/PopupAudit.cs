using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using Superb.Desktop;
using Superb.Desktop.Platform;
using Superb.Core;

namespace Superb.WindowsTests;

internal static class PopupAudit
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Settle()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); Native.DwmFlush();
    }

    internal static int Run(string[] args)
    {
        var app = new App(isolatedTestHost: true); app.InitializeComponent();
        var reader = new ReaderWindow();
        var observations = new List<object>(); var snapshots = new List<object>();
        var failures = 0;
        void Observe(string label, VisualHandle visual)
        {
            Pump();
            var source = (HwndSource?)PresentationSource.FromVisual(visual.Element);
            var hwnd = source?.Handle ?? 0;
            var readable = Native.GetWindowDisplayAffinity(hwnd, out var affinity);
            var visible = Native.IsWindowVisible(hwnd);
            var contentVisible = source?.RootVisual is UIElement { IsVisible: true } && visual.Element is UIElement { IsVisible: true };
            var protectedOwner = reader.Check();
            observations.Add(new { label, hwnd = $"0x{hwnd:X}", visible, contentVisible, readable, affinity, protectedOwner });
            snapshots.Add(WindowAudit.Snapshot(Environment.ProcessId));
            if (!visible || !contentVisible || !readable || affinity != 0x11 || !protectedOwner) failures++;
        }
        try
        {
            reader.Configure(new Configuration().Scenes, "general", "Synthetic recipient", "Ctrl+Alt+E");
            reader.SetActions(true, false, false, false, false, false);
            reader.Show(); Pump();
            snapshots.Add(WindowAudit.Snapshot(Environment.ProcessId));
            var picker = (ComboBox)reader.FindName("WorkspaceScene");
            picker.IsDropDownOpen = true; Pump();
            var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
            Observe("reader-scene-dropdown", new(popup.Child));
            picker.IsDropDownOpen = false; Pump();
            picker.IsDropDownOpen = true; Pump();
            Observe("reader-scene-dropdown-reopen", new(popup.Child));
            picker.IsDropDownOpen = false; Pump();
            var tooltip = new ToolTip { Content = "Synthetic shortcut description", PlacementTarget = picker, StaysOpen = true };
            picker.ToolTip = tooltip; tooltip.IsOpen = true; Pump();
            Observe("tooltip", new(tooltip));
            tooltip.IsOpen = false; Pump();
            tooltip.IsOpen = true; Pump(); Observe("tooltip-reopen", new(tooltip)); tooltip.IsOpen = false; Pump();
            var menu = new ContextMenu { PlacementTarget = (Button)reader.FindName("SettingsButton") };
            menu.Items.Add(new MenuItem { Header = "Synthetic context action" });
            var submenu = new MenuItem { Header = "Synthetic submenu" }; submenu.Items.Add(new MenuItem { Header = "Synthetic child action" }); menu.Items.Add(submenu);
            menu.IsOpen = true; Pump(); Observe("context-menu", new(menu));
            submenu.IsSubmenuOpen = true; Pump();
            var submenuPopup = (Popup)submenu.Template.FindName("PART_Popup", submenu);
            Observe("nested-context-menu", new(submenuPopup.Child));
            menu.IsOpen = false; Pump();
            foreach (var label in new[] { "dropdown", "tooltip", "context-menu" })
            {
                reader.Render("Synthetic private answer", "Synthetic");
                ((Expander)reader.FindName("ComposerExpander")).IsExpanded = true;
                System.Windows.Media.Visual element;
                if (label == "dropdown") { picker.IsDropDownOpen = true; element = popup.Child; }
                else if (label == "tooltip") { tooltip.IsOpen = true; element = tooltip; }
                else { menu.IsOpen = true; element = menu; }
                Pump();
                var source = (HwndSource)PresentationSource.FromVisual(element);
                var removed = Native.SetWindowDisplayAffinity(source.Handle, 0);
                var rejected = !reader.Check();
                var cleared = ((AnswerView)reader.FindName("Answer")).Text.Length == 0;
                var suppressed = ((UIElement)source.RootVisual).Visibility == Visibility.Collapsed;
                observations.Add(new { label = label + "-protection-loss", removed, rejected, cleared, suppressed });
                if (!removed || !rejected || !cleared || !suppressed) failures++;
                picker.IsDropDownOpen = false; tooltip.IsOpen = false; menu.IsOpen = false; Pump();
                if (label == "dropdown") picker.IsDropDownOpen = true;
                else if (label == "tooltip") tooltip.IsOpen = true;
                else menu.IsOpen = true;
                Pump();
                var reopened = (HwndSource)PresentationSource.FromVisual(element);
                var recovered = ((UIElement)reopened.RootVisual).IsVisible && Native.GetWindowDisplayAffinity(reopened.Handle, out var reopenedAffinity) && reopenedAffinity == 0x11 && reader.Check();
                observations.Add(new { label = label + "-reopen-after-loss", recovered });
                if (!recovered) failures++;
                picker.IsDropDownOpen = false; tooltip.IsOpen = false; menu.IsOpen = false; Pump();
            }
            var backend = new Backend("test", "Synthetic private backend", "https://example.test/v1/chat/completions", "model", "");
            var settings = new MainWindow(new Configuration { Backends = [backend], SelectedBackendId = backend.Id }, new NoRequestsBackend());
            try
            {
                settings.Show(); Pump();
                ((TabControl)settings.FindName("MainTabs")).SelectedItem = settings.FindName("ScenesTab"); Pump();
                var backendPicker = (ComboBox)settings.FindName("SceneBackendPicker");
                backendPicker.IsDropDownOpen = true; Pump();
                var backendPopup = (Popup)backendPicker.Template.FindName("PART_Popup", backendPicker);
                Observe("settings-backend-dropdown", new(backendPopup.Child)); backendPicker.IsDropDownOpen = false; Pump();
            }
            finally { settings.ExitApplication(); Pump(); }
            if (args.Contains("--capture-check"))
            {
                reader.Hide();
                var background = new Border { Background = Brushes.Lime };
                var backdrop = new Window { Title = "Superb synthetic capture control", Content = background,
                    Left = SystemParameters.WorkArea.Left + 40, Top = SystemParameters.WorkArea.Top + 40,
                    Width = 500, Height = 380, WindowStyle = WindowStyle.None, Topmost = true };
                var marker = new ToolTip { PlacementTarget = background, Placement = PlacementMode.Relative,
                    HorizontalOffset = 100, VerticalOffset = 100, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                    Content = new Border { Width = 180, Height = 100, Background = Brushes.Magenta }, StaysOpen = true };
                try
                {
                    backdrop.Show(); Settle(); marker.IsOpen = true; Settle();
                    var markerHandle = ((HwndSource)PresentationSource.FromVisual(marker)).Handle;
                    Native.GetWindowRect(markerHandle, out var markerRect);
                    Native.GetWindowRect(new WindowInteropHelper(backdrop).Handle, out var backdropRect);
                    var center = new Native.Point { X = (markerRect.Left + markerRect.Right) / 2, Y = (markerRect.Top + markerRect.Bottom) / 2 };
                    var monitor = CaptureService.MonitorAt(center, CaptureService.Monitors());
                    var bounds = new PixelRect(center.X - 16, center.Y - 16, 32, 32);
                    if (bounds.X < backdropRect.Left || bounds.Y < backdropRect.Top || bounds.X + 32 > backdropRect.Right || bounds.Y + 32 > backdropRect.Bottom)
                        throw new InvalidOperationException("Synthetic capture region outside test backdrop");
                    var target = monitor with { Kind = CaptureKind.Region, Bounds = bounds, MonitorBounds = monitor.Bounds };
                    var captures = new List<object>();
                    foreach (var protect in new[] { false, true })
                    {
                        if (!Native.SetWindowDisplayAffinity(markerHandle, protect ? 0x11u : 0u)) throw new InvalidOperationException("Synthetic affinity control failed");
                        Settle();
                        for (var sample = 0; sample < 3; sample++)
                        {
                            var captured = CaptureService.Capture(target);
                            var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(ImageProcessor.Decode(captured.Png), PixelFormats.Bgra32, null, 0);
                            var pixels = new byte[32 * 32 * 4]; bitmap.CopyPixels(pixels, 32 * 4, 0);
                            var magenta = 0; var green = 0;
                            for (var index = 0; index < pixels.Length; index += 4)
                            {
                                if (pixels[index] > 230 && pixels[index + 1] < 25 && pixels[index + 2] > 230) magenta++;
                                if (pixels[index] < 25 && pixels[index + 1] > 230 && pixels[index + 2] < 25) green++;
                            }
                            var expected = protect ? green : magenta;
                            Native.GetWindowDisplayAffinity(markerHandle, out var actualAffinity);
                            var rootVisible = ((UIElement)((HwndSource)PresentationSource.FromVisual(marker)).RootVisual).IsVisible;
                            var markerVisible = marker.IsOpen && marker.IsVisible && marker.Content is UIElement { IsVisible: true };
                            var passed = expected >= 1000 && actualAffinity == (protect ? 0x11u : 0u) && Native.IsWindowVisible(markerHandle) && rootVisible && markerVisible;
                            captures.Add(new { protect, sample, actualAffinity, visible = Native.IsWindowVisible(markerHandle), rootVisible, markerVisible,
                                magentaPixels = magenta, backdropPixels = green, totalPixels = 1024, passed });
                            if (!passed) failures++;
                            Array.Clear(pixels);
                        }
                    }
                    observations.Add(new { label = "gdi-tooltip-steady-state", captures, scope = "32x32 synthetic patch; BitBlt with CAPTUREBLT only; no first-frame or other capture-chain claim" });
                }
                catch (Exception error) { observations.Add(new { label = "gdi-tooltip-steady-state", error = error is SafeFailure safe ? safe.Code : error.GetType().Name }); failures++; }
                finally { marker.IsOpen = false; backdrop.Close(); Pump(); }
            }
        }
        finally { reader.Finish(); Pump(); }
        var directoryIndex = Array.IndexOf(args, "--output-dir");
        var directory = directoryIndex >= 0 ? args[directoryIndex + 1] : "artifacts/popup-audit";
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "window-audit.json"), JsonSerializer.Serialize(WindowAudit.Report(Environment.ProcessId, snapshots, true), options));
        File.WriteAllText(Path.Combine(directory, "popup-results.json"), JsonSerializer.Serialize(new { observations, failures, note = "Native HWND properties of production controls, synthetic content. Not frame-by-frame capture verification." }, options));
        Console.WriteLine(JsonSerializer.Serialize(new { observations, failures }));
        return args.Contains("--require-protected") && failures > 0 ? 1 : 0;
    }
    private sealed record VisualHandle(System.Windows.Media.Visual Element);
    private sealed class NoRequestsBackend : IBackendClient
    {
        public IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("This audit must never send requests.");
    }
}
