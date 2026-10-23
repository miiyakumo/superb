using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

// Read-only observer. No hooks, activation, input injection, pixels, UIA text or clipboard access.
internal static class WindowAudit
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    internal static object Snapshot(int targetPid, bool allWindows = false)
    {
        var windows = new List<object>();
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var processId);
            var target = processId == targetPid;
            if (!target && !allWindows) return true;
            var title = new StringBuilder(256);
            if (target) GetWindowTextW(window, title, title.Capacity);
            var className = new StringBuilder(256); GetClassNameW(window, className, className.Capacity);
            string processName;
            try { using var process = Process.GetProcessById((int)processId); processName = process.ProcessName; }
            catch { processName = "unavailable"; }
            var style = Native.GetWindowLongPtr(window, -16).ToInt64();
            var exStyle = Native.GetWindowLongPtr(window, -20).ToInt64();
            var affinityOk = GetWindowDisplayAffinity(window, out var affinity);
            int? affinityError = affinityOk ? null : Marshal.GetLastWin32Error();
            var rectOk = Native.GetWindowRect(window, out var rect);
            var cloakOk = DwmGetWindowAttribute(window, 14, out var cloak, sizeof(int)) == 0;
            windows.Add(new
            {
                hwnd = $"0x{window:X}", processId, processName, isTarget = target,
                title = target ? title.ToString() : null, className = className.ToString(),
                isVisible = Native.IsWindowVisible(window), isTopmost = (exStyle & 8) != 0,
                isToolWindow = (exStyle & 0x80) != 0, isNoActivate = (exStyle & 0x08000000) != 0,
                isTransparent = (exStyle & 0x20) != 0, isLayered = (exStyle & 0x80000) != 0,
                isCloaked = cloakOk ? (bool?)(cloak != 0) : null,
                ownerHwnd = $"0x{GetWindow(window, 4):X}", style = $"0x{style:X}", exStyle = $"0x{exStyle:X}",
                displayAffinity = affinityOk ? (uint?)affinity : null, affinityError,
                rect = rectOk ? new { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom } : null
            });
            return true;
        }, 0);
        return new { timestamp = DateTimeOffset.UtcNow, foregroundHwnd = $"0x{Native.GetForegroundWindow():X}", windows };
    }

    internal static object Report(int targetPid, IReadOnlyList<object> snapshots, bool synthetic, bool allWindows = false, string[]? notes = null) => new
    {
        schema = "superb-window-audit/v1", capturedAt = DateTimeOffset.UtcNow,
        source = new
        {
            tool = "Superb.WindowsTests.WindowAudit", version = "1", scope = allWindows ? "desktop-top-level-redacted" : "target-process-top-level",
            synthetic, limitations = new[] { "Snapshot sampling can miss short-lived windows.", "Other-process titles are omitted; affinity read failure is unknown, not WDA_NONE.", "Styles and window presence do not establish maliciousness or capture-chain protection." }
        },
        environment = new { os = Environment.OSVersion.VersionString, observerPid = Environment.ProcessId, targetPid, sessionId = Process.GetCurrentProcess().SessionId },
        snapshots, notes = notes ?? []
    };

    internal static int Run(string[] args)
    {
        string? Value(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        if (!int.TryParse(Value("--target-pid"), out var targetPid) || targetPid <= 0)
        { Console.Error.WriteLine("--target-pid is required; use only a process in the authorized test scope."); return 2; }
        var samples = int.TryParse(Value("--samples"), out var count) ? Math.Clamp(count, 1, 500) : 1;
        var interval = int.TryParse(Value("--interval-ms"), out var delay) ? Math.Clamp(delay, 50, 10000) : 250;
        var allWindows = args.Contains("--all-windows");
        var snapshots = new List<object>();
        var journalPath = Value("--journal");
        if (journalPath is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        using var journal = journalPath is null ? null : new StreamWriter(journalPath, append: true) { AutoFlush = true };
        for (var index = 0; index < samples; index++)
        {
            var snapshot = Snapshot(targetPid, allWindows);
            snapshots.Add(snapshot);
            journal?.WriteLine(JsonSerializer.Serialize(new { schema = "superb-window-audit-sample/v1", targetPid, snapshot }));
            if (index + 1 < samples) Thread.Sleep(interval);
        }
        var json = JsonSerializer.Serialize(Report(targetPid, snapshots, args.Contains("--synthetic"), allWindows), new JsonSerializerOptions { WriteIndented = true });
        if (Value("--output") is { } output) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, json); Console.WriteLine($"Saved {samples} snapshot(s) to {Path.GetFullPath(output)}"); }
        else Console.WriteLine(json);
        return 0;
    }
}
