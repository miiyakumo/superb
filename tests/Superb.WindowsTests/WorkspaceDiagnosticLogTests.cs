using System.IO;
using System.Text.Json;
using Superb.Desktop;

namespace Superb.WindowsTests;

internal static class WorkspaceDiagnosticLogTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Diagnostic assertion failed"); }
    internal static void Add(List<(string, Action)> tests)
    {
        tests.Add(("Workspace diagnostic writer redacts unknown errors rejects enums and bounds rotation", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "superb-workspace-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var writer = new WorkspaceDiagnosticLog(directory, 8192);
                var entry = new WorkspaceDiagnosticEvent(Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                    WorkspaceEvent.AcquisitionFailed, WorkspaceReason.Clipboard,
                    new(WorkspaceSceneKind.Custom, Guid.NewGuid()), new([], 0, 0, 0), Guid.NewGuid(),
                    ErrorCode: "SENTINEL-private-exception");
                Check(writer.Write(entry));
                Check(!File.ReadAllText(writer.FilePath).Contains("SENTINEL"));
                Check(!writer.Write(entry with { Event = (WorkspaceEvent)999 }));
                Check(!writer.Write(entry with { Scene = new((WorkspaceSceneKind)999, Guid.NewGuid()) }));
                Check(File.ReadAllLines(writer.FilePath).Length == 1);
                for (var i = 2; i < 100; i++) Check(writer.Write(entry with { Sequence = i, ErrorCode = "clipboard_busy" }));
                var files = Directory.GetFiles(directory, "workspace*.jsonl");
                Check(files.Length == 4 && files.All(f => new FileInfo(f).Length <= 8192));
                foreach (var path in files) foreach (var line in File.ReadLines(path))
                {
                    using var parsed = JsonDocument.Parse(line);
                    Check(parsed.RootElement.GetProperty("AppInstanceId").GetGuid() == entry.AppInstanceId);
                }
                var blocker = Path.Combine(directory, "file"); File.WriteAllText(blocker, "synthetic");
                var unavailable = new WorkspaceDiagnosticLog(blocker);
                Check(!unavailable.Write(entry) && !unavailable.Available);
            }
            finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }));
    }
}
