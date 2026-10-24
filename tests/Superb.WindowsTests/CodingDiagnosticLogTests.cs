using System.IO;
using System.Text.Json;
using Superb.Core;
using Superb.Core.Coding;
using Superb.Desktop;

namespace Superb.WindowsTests;

internal static class CodingDiagnosticLogTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Diagnostic assertion failed"); }
    internal static void Add(List<(string, Action)> tests)
    {
        tests.Add(("Coding diagnostics persist an actual failed run without any source, endpoint, model or secret text", () =>
        {
            InDirectory(directory =>
            {
                const string sensitive = "SENTINEL-private-source-and-key";
                var writer = new CodingDiagnosticLog(directory);
                var session = new CodingSession();
                var payload = Payload.FromText(sensitive);
                session.AddObservation(new(Guid.NewGuid(), InputKind.ManualText, payload, 0, DateTimeOffset.UtcNow));
                var runner = new CodingAgentRunner(new FailingModel(sensitive));
                runner.Diagnostic += item => writer.Write(item);
                runner.RunAsync(session, new("sensitive-id", sensitive, "https://private.invalid/v1/chat/completions", sensitive, sensitive)).GetAwaiter().GetResult();
                var lines = File.ReadAllLines(writer.FilePath);
                Check(lines.Length >= 4 && writer.Available);
                var joined = string.Join("\n", lines);
                Check(!joined.Contains(sensitive) && !joined.Contains("private.invalid") && !joined.Contains("sensitive-id"));
                using var last = JsonDocument.Parse(lines[^1]);
                Check(last.RootElement.GetProperty("Event").GetString() == "Failed");
                Check(last.RootElement.GetProperty("ErrorCode").GetString() == "backend_request_failed");
                Check(last.RootElement.GetProperty("ModelCalls").GetInt32() == 1);
                Check(last.RootElement.TryGetProperty("RunId", out _) && last.RootElement.TryGetProperty("ElapsedMilliseconds", out _));
            });
        }));
        tests.Add(("Coding diagnostic rotation remains bounded and each record is readable JSON", () =>
        {
            InDirectory(directory =>
            {
                var writer = new CodingDiagnosticLog(directory, 1024);
                var id = Guid.NewGuid();
                for (var i = 1; i <= 80; i++)
                    Check(writer.Write(new(id, i, DateTimeOffset.UtcNow, CodingDiagnosticKind.CallStarted, CodingRunState.Analyzing,
                        i, i * 100, ErrorCode: "http_401")));
                var files = Directory.GetFiles(directory, "coding*.jsonl");
                Check(files.Length == 4 && files.All(path => new FileInfo(path).Length <= 1024));
                foreach (var file in files) foreach (var line in File.ReadAllLines(file))
                    using (var document = JsonDocument.Parse(line)) Check(document.RootElement.GetProperty("RunId").GetGuid() == id);
            });
        }));
        tests.Add(("Unavailable diagnostic storage reports failure without throwing", () =>
        {
            InDirectory(directory =>
            {
                var blocker = Path.Combine(directory, "file"); File.WriteAllText(blocker, "synthetic");
                var writer = new CodingDiagnosticLog(blocker);
                Check(!writer.Write(new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, CodingDiagnosticKind.Started,
                    CodingRunState.Analyzing, 0, 0)));
                Check(!writer.Available);
            });
        }));
        tests.Add(("Transport diagnostics persist numeric metrics and revalidate request ID fingerprints", () =>
        {
            InDirectory(directory =>
            {
                var writer = new CodingDiagnosticLog(directory);
                var metrics = new CodingTransportTrace().Snapshot with
                {
                    Stage = CodingTransportStage.ReadingResponseBody, ResponseBytes = 128, FirstByteMilliseconds = 40,
                    MillisecondsSinceLastByte = 900, RequestIdFingerprint = "SENTINEL-private-header", ServerProcessingMilliseconds = double.NaN
                };
                Check(writer.Write(new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, CodingDiagnosticKind.CallFailed,
                    CodingRunState.Analyzing, 1, 1000, ErrorCode: "request_timed_out", Transport: metrics)));
                var text = File.ReadAllText(writer.FilePath);
                Check(!text.Contains("SENTINEL") && !text.Contains("NaN"));
                using var document = JsonDocument.Parse(text);
                var transport = document.RootElement.GetProperty("Transport");
                Check(transport.GetProperty("Stage").GetString() == "ReadingResponseBody" && transport.GetProperty("ResponseBytes").GetInt64() == 128);
                Check(transport.GetProperty("RequestIdFingerprint").ValueKind == JsonValueKind.Null);
                metrics = metrics with { Stage = (CodingTransportStage)999 };
                Check(writer.Write(new(Guid.NewGuid(), 2, DateTimeOffset.UtcNow, CodingDiagnosticKind.Transport,
                    CodingRunState.Analyzing, 1, 2000, Transport: metrics)));
                using var invalid = JsonDocument.Parse(File.ReadAllLines(writer.FilePath)[^1]);
                Check(invalid.RootElement.GetProperty("Transport").ValueKind == JsonValueKind.Null);
            });
        }));
    }
    private sealed class FailingModel(string sensitive) : IAgentModelClient
    {
        public Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken cancellationToken) =>
            Task.FromException<AgentAction>(new SafeFailure(sensitive));
    }
    private static void InDirectory(Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "superb-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { test(path); }
        finally
        {
            foreach (var file in Directory.GetFiles(path)) File.Delete(file);
            Directory.Delete(path);
        }
    }
}
