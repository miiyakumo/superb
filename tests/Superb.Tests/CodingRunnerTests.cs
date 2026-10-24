using System.Collections.Immutable;
using System.Text.Json;
using Superb.Core;
using Superb.Core.Coding;

internal static class CodingRunnerTests
{
    private static readonly Backend Backend = new("coding-test", "synthetic", "https://example.test/v1/chat/completions", "test", "");
    internal static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Func<Task> body) => tests.Add(("Coding runner: " + name, body));
        Test("model verification checklists retain every item and reject nontext items", () =>
        {
            var input = JsonSerializer.SerializeToElement(new { conclusion = "Cause", proposedChange = "Fix", correctness = "Invariant",
                complexity = "O(n log n)", checks = new[] { "duplicates => 0", "input unchanged" }, evidence = Array.Empty<EvidenceRef>() }, CodingJson.Options);
            var parsed = CodingJson.Read<TechnicalAnalysis>(input);
            Assert(parsed.Checks.Contains("duplicates => 0") && parsed.Checks.Contains("input unchanged"));
            using var invalid = JsonDocument.Parse("{\"conclusion\":\"cause\",\"checks\":[{\"run\":\"shell\"}]}");
            try { CodingJson.Read<TechnicalAnalysis>(invalid.RootElement); throw new Exception("Nontext checklist accepted"); }
            catch (JsonException) { }
            return Task.CompletedTask;
        });
        Test("publishes grounded memory and reviewed directive atomically", async () =>
        {
            var (session, observation) = Session();
            var directive = Instruction(observation);
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("stage_memory_changes", new { facts = Facts(observation).Facts }), // Empty assessment patch may be omitted.
                2 => Action("publish_directive", directive),
                3 => Review(observation),
                _ => throw new Exception("Unexpected extra request")
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && runner.Latest.Calls == 3);
            Assert(session.Memory.Facts.Length == 1 && session.Memory.Attempts.Length == 1);
            Assert(session.CurrentDirective?.Prompt == directive.Prompt + "\n\n" + CodingDirectivePolicy.ExecutionBoundary && !runner.IsRunning && !runner.CanRetry);
            Assert(model.Requests.Last().Content.Text!.Contains(CodingJson.Serialize(session.CurrentDirective!.Prompt)), "Reviewer must receive the exact prompt including execution boundaries");
        });

        Test("missing task memory cannot publish even when model tries", async () =>
        {
            var (session, observation) = Session();
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("publish_directive", Instruction(observation)),
                2 => Action("stage_memory_changes", Facts(observation)),
                3 => Action("publish_directive", Instruction(observation)),
                4 => Review(observation),
                _ => throw new Exception("Unexpected extra request")
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests.Count == 4);
            Assert(model.Requests[1].Content.Text!.Contains("Persist the grounded task"));
        });

        Test("rejected review causes revision before any memory publication", async () =>
        {
            var (session, observation) = Session();
            var original = Instruction(observation);
            var changed = original with { Prompt = "Read the actual Solve function, return its full current text and test output; do not edit yet.", Strategy = "gather-current-source" };
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("stage_memory_changes", Facts(observation)),
                2 => Action("publish_directive", original),
                3 => Action("review_result", new SolutionReview(false, "Current function is missing; obtain it before patching.", [Ref(observation)])),
                4 => session.Memory.Revision == 0 ? Action("publish_directive", changed) : throw new Exception("Premature commit"),
                5 => Review(observation),
                _ => throw new Exception("Unexpected extra request")
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(session.Memory.Attempts.Length == 1 && session.CurrentDirective!.Prompt == changed.Prompt + "\n\n" + CodingDirectivePolicy.ExecutionBoundary);
        });

        Test("stop discards staged edits and late results; retry freezes original evidence", async () =>
        {
            var (session, observation) = Session();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<AgentAction>(TaskCreationOptions.RunContinuationsAsynchronously);
            var model = new AsyncModel(async (call, request, token) =>
            {
                if (call == 1) return Action("stage_memory_changes", Facts(observation));
                if (call == 2) { entered.SetResult(); return await release.Task; }
                if (call == 3) return Action("stage_memory_changes", Facts(observation));
                if (call == 4) return Action("publish_directive", Instruction(observation));
                return Review(observation);
            });
            var runner = new CodingAgentRunner(model);
            var running = runner.RunAsync(session, Backend);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            runner.Stop();
            var late = session.AddObservation(Input("NEW MATERIAL MUST NOT ENTER RETRY"));
            release.SetResult(Action("publish_directive", Instruction(observation)));
            await running;
            Assert(session.Memory.Revision == 0 && runner.Latest?.State == CodingRunState.Cancelled && runner.CanRetry);
            await runner.RetryAsync(session);
            Assert(session.Memory.Attempts.Length == 1 && session.Observations.Count == 2);
            Assert(!model.Requests[2].Content.Text!.Contains(late.Id.ToString()));
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback);
            runner.Clear(); session.Clear();
            Assert(!runner.CanRetry && runner.Latest is null && session.Observations.Count == 0);
        });

        Test("new material during a run stays outside frozen context and cannot be cited", async () =>
        {
            var (session, observation) = Session();
            Observation? pending = null;
            var model = new ScriptedModel((call, request) =>
            {
                if (call == 1)
                {
                    pending = session.AddObservation(Input("NEW CODE PENDING"));
                    return Action("stage_memory_changes", Facts(observation));
                }
                Assert(!request.Content.Text!.Contains(pending!.Id.ToString()));
                return call == 2 ? Action("publish_directive", Instruction(observation)) : Review(observation);
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && session.Memory.Attempts.Length == 1);
        });

        Test("tool loops and call budgets stop without committing staged memory", async () =>
        {
            foreach (var budget in new[] { 2, 12 })
            {
                var (session, observation) = Session();
                var model = new ScriptedModel((call, request) => Action("read_task_state", new { }));
                var runner = new CodingAgentRunner(model);
                await runner.RunAsync(session, Backend, new(MaximumCalls: budget));
                Assert(runner.Latest?.State == CodingRunState.Failed && session.Memory.Revision == 0);
                Assert(runner.Latest!.Message == (budget == 2 ? "coding_call_budget_exhausted" : "coding_internal_loop"));
            }
        });

        Test("invalid evidence is repaired without exposing raw protocol as a directive", async () =>
        {
            var (session, observation) = Session();
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("stage_memory_changes", new MemoryChanges([new("bad", "contract", "fabrication", [new(Guid.NewGuid(), 0, 10)])], [])),
                2 => Action("stage_memory_changes", Facts(observation)),
                3 => Action("publish_directive", Instruction(observation)),
                _ => Review(observation)
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(session.Memory.Facts.All(f => f.Id != "bad") && session.Memory.Attempts.Length == 1);
            Assert(model.Requests[1].Content.Text!.Contains("coding_evidence_invalid"));
        });

        Test("semantic outcomes cannot be promoted from platform claims by missing execution evidence", async () =>
        {
            var (session, observation) = Session();
            var attempt = session.Commit(session.Freeze(), Facts(observation), Instruction(observation));
            var invalid = new AttemptAssessment(attempt.Id, "timeout", "constant", AttemptOutcome.Verified, true, false, [Ref(observation)]);
            var model = new ScriptedModel((call, request) => Action("stage_memory_changes", new MemoryChanges([], [invalid])));
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.Failed && session.Memory.Attempts[0].Assessment is null);
        });

        Test("repeated timeout forces independent backend diagnosis before a concrete algorithm directive", async () =>
        {
            var session = new CodingSession();
            var source = session.AddObservation(Input("Count inversions, n <= 200000; time limit 2s. Preserve Solve(int[] a). Current code: long Solve(int[] a) { long count=0; for(int i=0;i<a.Length;i++) for(int j=i+1;j<a.Length;j++) if(a[i]>a[j]) count++; return count; }"));
            var first = session.Commit(session.Freeze(), Facts(source), Instruction(source) with { Strategy = "constant-factor" });
            var feedback1 = session.AddObservation(Input("Run 1: replaced list allocation; n=200000 reverse sorted input timed out after 2s."));
            var second = session.Commit(session.Freeze(), new([], [Assessment(first.Id, feedback1)]), Instruction(source) with { Strategy = "constant-factor" });
            var feedback2 = session.AddObservation(Input("Run 2: cached length; same reverse sorted n=200000 test still timed out after 2s."));
            var replacement = Instruction(source) with
            {
                Direction = "Replace quadratic pair counting with merge-sort inversion counting",
                Strategy = "merge-sort-n-log-n",
                Prompt = "In Solve, replace the nested pair scan with merge-sort inversion counting. Count cross inversions when taking an element from the right half, use a 64-bit result, preserve the input signature. Test [3,2,1] => 3 and reverse sorted n=200000 => 19999900000. Return the changed function and actual test output. If Solve differs from the observed source, return current source before editing.",
                ExpectedEffect = "Reduce O(n²) to O(n log n) without changing outputs"
            };
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("stage_memory_changes", new MemoryChanges([], [Assessment(second.Id, feedback2)])),
                2 => Action("publish_directive", replacement), // A plausible prompt alone cannot bypass diagnosis.
                3 => Action("analyze_issue", new { question = "Repeated constant-factor attempts fail; derive a lower-complexity solution from constraints", evidence = new[] { Ref(source), Ref(feedback1), Ref(feedback2) } }),
                4 => Action("analysis_result", new TechnicalAnalysis("A pairwise scan is quadratic; n=200000 exceeds the budget.",
                    "Merge-sort inversion count; add leftRemaining for each smaller right item, use Int64.",
                    "The merge partitions within-half and cross inversions without double counting.", "O(n log n) time, O(n) auxiliary storage", "[3,2,1] => 3; descending 200000 => 19999900000", [Ref(source), Ref(feedback1), Ref(feedback2)])),
                5 => Action("publish_directive", replacement),
                6 => Review(source),
                _ => throw new Exception("Unexpected extra request")
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests.Count == 6);
            Assert(model.Requests[2].Content.Text!.Contains("requires analyze_issue"));
            Assert(model.Requests[3].SystemPrompt.Contains("技术诊断专家"));
            Assert(session.Memory.Attempts.Length == 3 && session.CurrentDirective!.Strategy == "merge-sort-n-log-n");
            Assert(session.Memory.Facts.Any(f => f.Kind == "conclusion"));
            var latest = session.Memory.Attempts.Last();
            session.AddObservation(Input("No new test run yet; platform asks which output to copy."));
            var followupModel = new ScriptedModel((call, request) => call switch
            {
                1 => Action("stage_memory_changes", new MemoryChanges([], [new(latest.Id, "timeout", "merge-sort-n-log-n", AttemptOutcome.Unknown, false, false, [])])),
                2 => Action("publish_directive", Instruction(source)),
                _ => Review(source)
            });
            var followup = new CodingAgentRunner(followupModel);
            await followup.RunAsync(session, Backend);
            Assert(followup.Latest?.State == CodingRunState.AwaitingFeedback && followupModel.Requests.Count == 3,
                "Already diagnosed evidence must not cause repeated expert calls while asking for execution feedback.");
        });

        Test("successive runs cannot bypass evaluating the previous execution", async () =>
        {
            var (session, source) = Session();
            var attempt = session.Commit(session.Freeze(), Facts(source), Instruction(source));
            var feedback = session.AddObservation(Input("agent says done but provides neither code nor test output"));
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("publish_directive", Instruction(source)),
                2 => Action("stage_memory_changes", new MemoryChanges([], [new(attempt.Id, "timeout", "gather-evidence", AttemptOutcome.Claimed, false, false, [Ref(feedback)])])),
                3 => Action("publish_directive", Instruction(source)),
                _ => Review(source)
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(model.Requests[1].Content.Text!.Contains("Assess the prior attempt"));
            Assert(session.Memory.Attempts[0].Assessment?.Outcome == AttemptOutcome.Claimed, runner.Latest?.Message);
            Assert(!session.CurrentDirective!.TaskComplete);
        });

        Test("long memory remains retrievable without stuffing every old directive into context", async () =>
        {
            var (session, source) = Session();
            var facts = Enumerable.Range(0, 300).Select(i => new MemoryFact($"record-{i:D3}", "contract",
                $"constraint {i}: " + new string('x', 2000), [Ref(source)])).ToImmutableArray();
            var prior = session.Commit(session.Freeze(), new(facts, []), Instruction(source));
            var model = new ScriptedModel((call, request) => call switch
            {
                1 => Action("read_task_state", new { query = "record-000", limit = 1 }),
                2 => request.Content.Text!.Contains("constraint 0:")
                    ? Action("stage_memory_changes", new MemoryChanges([], [new(prior.Id, "timeout", "gather-evidence", AttemptOutcome.Unknown, false, false, [])]))
                    : throw new Exception("Old fact was not retrieved"),
                3 => Action("publish_directive", Instruction(source)),
                _ => Review(source)
            });
            var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && session.Memory.Facts.Length == 300, runner.Latest?.Message);
            Assert(model.Requests[0].Content.Text!.Length < 100000);
        });
    }

    private static AttemptAssessment Assessment(Guid attempt, Observation feedback) => new(attempt, "timeout", "constant-factor",
        AttemptOutcome.Unchanged, true, true, [Ref(feedback)], [feedback.Id]);

    internal static AgentAction Action<T>(string name, T arguments) => new(name, JsonSerializer.SerializeToElement(arguments, CodingJson.Options));
    private static AgentAction Review(Observation observation) => Action("review_result", new SolutionReview(true, "Scoped evidence gathering; no unverified completion or fabricated path.", [Ref(observation)]));
    private static (CodingSession, Observation) Session()
    {
        var session = new CodingSession(); return (session, session.AddObservation(Input("Task: preserve Solve interface. Current source unknown. Test feedback: timeout.")));
    }
    private static InputSnapshot Input(string value) => new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText(value), 1, DateTimeOffset.UtcNow);
    private static EvidenceRef Ref(Observation o) => new(o.Id, 0, o.Snapshot.Content.Text!.Length);
    private static MemoryChanges Facts(Observation o) => new([new("contract", "contract", "Preserve Solve interface; source is not yet available.", [Ref(o)])], []);
    private static Directive Instruction(Observation o) => new("Get the current Solve function", "Locate Solve in the platform IDE",
        "Return the complete current Solve function, its input scale constraints and the full timeout test feedback. Do not edit yet.",
        "Current function and full test output", "timeout", "gather-evidence", "Enough evidence to distinguish algorithm cost from waiting", [Ref(o)]);
    private static void Assert(bool condition, string? detail = null) { if (!condition) throw new Exception("Coding runner assertion failed" + (detail is null ? "" : ": " + detail)); }
    private sealed class ScriptedModel(Func<int, AgentModelRequest, AgentAction> respond) : IAgentModelClient
    {
        public List<AgentModelRequest> Requests { get; } = [];
        public Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken token)
        { Requests.Add(request); return Task.FromResult(respond(Requests.Count, request)); }
    }
    private sealed class AsyncModel(Func<int, AgentModelRequest, CancellationToken, Task<AgentAction>> respond) : IAgentModelClient
    {
        public List<AgentModelRequest> Requests { get; } = [];
        public Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken token)
        { Requests.Add(request); return respond(Requests.Count, request, token); }
    }
}
