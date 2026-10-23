using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

// A synthetic, local-only visual fixture for external recorder validation.
// It neither controls nor inspects any other application.
internal static class CaptureTimeline
{
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    internal static int Run(string[] args)
    {
        var app = new App(isolatedTestHost: true); app.InitializeComponent();
        var canvas = new Canvas { Background = Brushes.Cyan };
        var reference = new Border { Width = 80, Height = 80, Background = Brushes.White };
        Canvas.SetLeft(reference, 100); Canvas.SetTop(reference, 100); canvas.Children.Add(reference);
        var label = new TextBlock { Foreground = Brushes.Black, FontSize = 30, Text = "Superb synthetic capture test" };
        Canvas.SetLeft(label, 100); Canvas.SetTop(label, 210); canvas.Children.Add(label);
        var window = new Window { Title = "Superb synthetic capture timeline", Content = canvas,
            Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true };
        ToolTip MakeMarker() => new() { PlacementTarget = canvas, Placement = PlacementMode.Relative,
            HorizontalOffset = 300, VerticalOffset = 300, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Content = new Border { Width = 200, Height = 120, Background = Brushes.Magenta }, StaysOpen = true };
        var records = new List<object>(); var failures = 0;
        var started = DateTimeOffset.UtcNow;
        void Observe(string phase, int cycle, ToolTip marker)
        {
            var source = PresentationSource.FromVisual(marker) as HwndSource;
            var handle = source?.Handle ?? 0;
            var readable = Native.GetWindowDisplayAffinity(handle, out var affinity);
            Native.GetWindowRect(handle, out var rect);
            var visible = Native.IsWindowVisible(handle) && marker.IsOpen && marker.IsVisible &&
                marker.Content is UIElement { IsVisible: true } && source?.RootVisual is UIElement { IsVisible: true };
            var passed = readable && affinity == (phase == "positive-control" ? 0u : 0x11u) && visible;
            records.Add(new { phase, cycle, utc = DateTimeOffset.UtcNow, handle = $"0x{handle:X}",
                affinity, readable, visible, rect = new { rect.Left, rect.Top, rect.Right, rect.Bottom }, passed });
            if (!passed) failures++;
        }
        try
        {
            window.Show(); Wait(600);
            label.Text = "POSITIVE CONTROL - intentionally visible synthetic marker";
            var control = MakeMarker(); control.IsOpen = true; Wait(200);
            var controlHandle = ((HwndSource)PresentationSource.FromVisual(control)).Handle;
            if (!Native.SetWindowDisplayAffinity(controlHandle, 0)) throw new InvalidOperationException("Control affinity failed");
            Wait(200); Observe("positive-control", 0, control); Wait(1800);
            control.IsOpen = false; Wait(500);
            canvas.Background = Brushes.Lime;
            label.Text = "PROTECTED - synthetic marker must be absent from recording";
            Wait(500);
            var reused = MakeMarker();
            for (var cycle = 1; cycle <= 30; cycle++)
            {
                var marker = cycle <= 20 ? MakeMarker() : reused;
                label.Text = $"PROTECTED - cycle {cycle}/30";
                records.Add(new { phase = "opening", cycle, utc = DateTimeOffset.UtcNow });
                marker.IsOpen = true;
                Wait(100); Observe("protected", cycle, marker); Wait(350);
                marker.IsOpen = false; Wait(200);
            }
            Wait(500);
            canvas.Background = Brushes.Cyan;
            label.Text = "FINAL POSITIVE CONTROL"; Wait(500);
            control = MakeMarker(); control.IsOpen = true; Wait(200);
            controlHandle = ((HwndSource)PresentationSource.FromVisual(control)).Handle;
            if (!Native.SetWindowDisplayAffinity(controlHandle, 0)) throw new InvalidOperationException("Final control affinity failed");
            Wait(200); Observe("positive-control", 31, control); Wait(1800);
            control.IsOpen = false; Wait(400);
        }
        finally { window.Close(); }
        var directoryIndex = Array.IndexOf(args, "--output-dir");
        var directory = directoryIndex >= 0 ? args[directoryIndex + 1] : "artifacts/capture-timeline";
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "timeline.json"), JsonSerializer.Serialize(new {
            started, ended = DateTimeOffset.UtcNow, failures, records,
            scope = "Synthetic ToolTip with production App resources and TransientSurfaceProtection; external recording required. No claim for all popup types or capture methods."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Capture timeline complete: {failures} fixture failures");
        return failures == 0 ? 0 : 1;
    }
}
