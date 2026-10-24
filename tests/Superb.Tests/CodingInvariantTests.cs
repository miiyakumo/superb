using System.Collections.Immutable;
using System.Text.Json;
using Superb.Core;
using Superb.Core.Coding;

internal static class CodingInvariantTests
{
    private static readonly Backend Backend = new("invariant-test", "synthetic", "https://example.test/v1/chat/completions", "test", "");

    internal static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Coding invariants: controller cannot create overwrite or replay reserved expert facts", async () =>
        {
            foreach (var mode in new[] { "create", "overwrite", "noop" })
            {
                var session = new CodingSession();
                var source = session.AddObservation(Input("Observed code and feedback"));
                var trusted = new MemoryFact("analysis:trusted", "conclusion", "Trusted expert conclusion", [Ref(source)]);
                var prior = session.Commit(session.Freeze(), new([trusted], []), Instruction(source));
                var injected = mode switch
                {
                    "create" => trusted with { Id = "analysis:forged", Text = "Forged expert result" },
                    "overwrite" => trusted with { Text = "Overwritten expert result" },
                    _ => trusted
                };
                var unknown = new AttemptAssessment(prior.Id, "timeout", "constant-factor", AttemptOutcome.Unknown, false, false, []);
                var model = new ScriptedModel((call, request) => call switch
                {
                    1 => Action("stage_memory_changes", new MemoryChanges([injected], [unknown])),
                    2 => request.Content.Text!.Contains("coding_memory_reserved_id") && PriorAssessmentIsNull(request)
                        ? Action("stage_memory_changes", new MemoryChanges([new("contract", "contract", "Preserve the signature", [Ref(source)])], [unknown]))
                        : throw new Exception("Reserved fact patch was not rejected atomically"),
                    3 => Action("publish_directive", Instruction(source)),
                    4 => Review(source),
                    _ => throw new Exception("Unexpected reserved-fact sequence")
                });
                var runner = new CodingAgentRunner(model);
                await runner.RunAsync(session, Backend);
                Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests.Count == 4, runner.Latest?.Message);
                Assert(session.Memory.Facts.Single(f => f.Id == trusted.Id) == trusted);
                Assert(session.Memory.Facts.All(f => f.Id != "analysis:forged"));
            }
        }));

        tests.Add(("Coding invariants: rejected expert result cannot satisfy the deep-analysis gate", async () =>
        {
            var session = new CodingSession();
            var source = session.AddObservation(Input("n <= 200000; Solve uses pairwise scan; preserve interface"));
            var first = session.Commit(session.Freeze(), Facts(source, "Preserve interface"), Instruction(source));
            var feedback1 = session.AddObservation(Input("Execution 1: same scale; constant optimization timed out"));
            var second = session.Commit(session.Freeze(), new([], [Assessment(first.Id, feedback1)]), Instruction(source));
            var feedback2 = session.AddObservation(Input("Execution 2: same scale; another constant optimization timed out"));
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("stage_memory_changes", new MemoryChanges([], [Assessment(second.Id, feedback2)])),
                2 => Action("analyze_issue", new { question = "Find the algorithm bottleneck", evidence = new[] { Ref(source) } }),
                3 => Action("analysis_result", Analysis(source) with { Conclusion = "INVALID_ANALYSIS_MUST_NOT_SURVIVE", Evidence = [new(Guid.NewGuid(), 0, 1)] }),
                4 => Action("publish_directive", Instruction(source)),
                5 => request.Content.Text!.Contains("requires analyze_issue") && !ContainsCurrentAnalysis(request)
                    ? Action("analyze_issue", new { question = "Find the bottleneck with valid source references", evidence = new[] { Ref(source) } })
                    : throw new Exception("Rejected expert result bypassed deep analysis or remained in current analysis"),
                6 => Action("analysis_result", Analysis(source)),
                7 => Action("publish_directive", Instruction(source)),
                8 => Review(source),
                _ => throw new Exception("Unexpected call after rejected analysis")
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests.Count == 8, runner.Latest?.Message);
            Assert(session.Memory.Facts.All(f => !f.Text.Contains("INVALID_ANALYSIS_MUST_NOT_SURVIVE")));
        }));

        tests.Add(("Coding invariants: a facts-only correction invalidates an accepted review", async () =>
        {
            var session = new CodingSession();
            var source = session.AddObservation(Input("Original signature must be preserved; integers may overflow 32 bit"));
            var directive = Instruction(source);
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("stage_memory_changes", Facts(source, "Preserve signature")),
                2 => Action("review_solution", directive),
                3 => Review(source),
                4 => Action("stage_memory_changes", Facts(source, "UPDATED_CONSTRAINT: 64-bit result required")),
                5 => Action("publish_directive", directive),
                6 => request.Content.Text!.Contains("UPDATED_CONSTRAINT") && request.SystemPrompt.Contains("方案审核者")
                    ? Review(source) : throw new Exception("Updated fact did not reach renewed review"),
                _ => throw new Exception("Unexpected facts-only review sequence")
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests.Count == 6, runner.Latest?.Message);
            Assert(session.Memory.Facts.Single().Text.Contains("UPDATED_CONSTRAINT"));
        }));

        tests.Add(("Coding invariants: oversized original read requests narrower evidence and can recover", async () =>
        {
            var session = new CodingSession();
            var source = session.AddObservation(Input(new string('中', 24000)));
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("read_evidence", new EvidenceRef(source.Id, 0, 24000)),
                2 => request.Content.Text!.Contains("coding_tool_result_too_large")
                    ? Action("read_evidence", new EvidenceRef(source.Id, 0, 1000))
                    : throw new Exception("Oversized evidence did not return a recoverable tool response"),
                3 => HasReadResult(request, 1000)
                    ? Action("stage_memory_changes", Facts(source, "Need the processing function before diagnosing"))
                    : throw new Exception("Narrower evidence was not supplied"),
                4 => Action("publish_directive", Instruction(source)),
                5 => Review(source),
                _ => throw new Exception("Unexpected oversized evidence sequence")
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests.Count == 5, runner.Latest?.Message);
            Assert(session.Observations.Single().Snapshot.Content.Text!.Length == 24000);
        }));
    }

    private static bool ContainsCurrentAnalysis(AgentModelRequest request)
    {
        using var doc = JsonDocument.Parse(request.Content.Text!);
        return doc.RootElement.GetProperty("analysis").ValueKind != JsonValueKind.Null;
    }
    private static bool PriorAssessmentIsNull(AgentModelRequest request)
    {
        using var doc = JsonDocument.Parse(request.Content.Text!);
        return doc.RootElement.GetProperty("memory").GetProperty("attempts")[0].GetProperty("assessment").ValueKind == JsonValueKind.Null;
    }
    private static bool HasReadResult(AgentModelRequest request, int length)
    {
        using var doc = JsonDocument.Parse(request.Content.Text!);
        return doc.RootElement.GetProperty("toolHistory").EnumerateArray().Any(item =>
            item.TryGetProperty("tool", out var tool) && tool.GetString() == "read_evidence" &&
            item.TryGetProperty("result", out var result) && result.TryGetProperty("text", out var text) && text.GetString()?.Length == length);
    }
    private static InputSnapshot Input(string text) => new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText(text), 1, DateTimeOffset.UtcNow);
    private static EvidenceRef Ref(Observation observation) => new(observation.Id, 0, Math.Min(10, observation.Snapshot.Content.Text!.Length));
    private static MemoryChanges Facts(Observation source, string text) => new([new("contract", "contract", text, [Ref(source)])], []);
    private static Directive Instruction(Observation source) => new("Get current source", "Locate Solve", "Return current Solve and complete test output; do not edit yet.",
        "Current function and test output", "timeout", "constant-factor", "Identify bottleneck", [Ref(source)]);
    private static AttemptAssessment Assessment(Guid id, Observation feedback) => new(id, "timeout", "constant-factor", AttemptOutcome.Unchanged,
        true, true, [Ref(feedback)], [feedback.Id]);
    private static TechnicalAnalysis Analysis(Observation source) => new("Pairwise scan is quadratic", "Use merge sort inversion counting",
        "Merge counts cross inversions exactly once", "O(n log n) time and O(n) space", "Reverse sorted n=200000 requires Int64", [Ref(source)]);
    private static AgentAction Review(Observation source) => Action("review_result", new SolutionReview(true, "Gathering current source is bounded and requires returned evidence", [Ref(source)]));
    private static AgentAction Action<T>(string name, T args) => new(name, JsonSerializer.SerializeToElement(args, CodingJson.Options));
    private static void Assert(bool condition, string? message = null) { if (!condition) throw new Exception("Coding invariant assertion failed: " + message); }
    private sealed class ScriptedModel(Func<int, AgentModelRequest, AgentAction> respond) : IAgentModelClient
    {
        public List<AgentModelRequest> Requests { get; } = [];
        public Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken token)
        { Requests.Add(request); return Task.FromResult(respond(Requests.Count, request)); }
    }
}
