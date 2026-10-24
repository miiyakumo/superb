using System.Collections.Immutable;
using Superb.Core;
using Superb.Core.Coding;

internal static class CodingMemoryTests
{
    public static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Action action) => tests.Add(("Coding memory: " + name, () => { action(); return Task.CompletedTask; }));

        Test("repeated capture retains one immutable original and never invents an attempt", () =>
        {
            var session = new CodingSession();
            var first = session.AddObservation(Input("old code", 1));
            var duplicate = session.AddObservation(Input("old code", 2));
            Assert(first == duplicate && session.Observations.Count == 1);
            Assert(first.Snapshot.ClipboardSequence == 1 && session.Memory.Attempts.IsEmpty);
            session.AddObservation(Input("different code", 3));
            Assert(session.Observations.Count == 2);
        });

        Test("new capture during analysis stays pending and does not enter its committed evidence", () =>
        {
            var session = new CodingSession();
            var original = session.AddObservation(Input("requirements"));
            var basis = session.Freeze();
            var pending = session.AddObservation(Input("new code"));
            var attempt = session.Commit(basis, Empty(), Instruction(original));
            Assert(session.Memory.Attempts.Length == 1 && session.Observations.Count == 2);
            Assert(attempt.Directive.Evidence[0].ObservationId == original.Id);
            Fails("coding_evidence_invalid", () => CodingSession.ValidateChanges(basis, Empty(), Instruction(pending)));
        });

        Test("commit validates atomically and retries cannot create duplicate attempts", () =>
        {
            var session = new CodingSession();
            var observation = session.AddObservation(Input("code"));
            var basis = session.Freeze();
            var fact = new MemoryFact("constraint", "requirement", "must preserve input", [Ref(observation)]);
            var invalid = new MemoryChanges([fact], [new(Guid.NewGuid(), "timeout", "constant", AttemptOutcome.Unknown, false, false, [])]);
            Fails("coding_assessment_invalid", () => session.Commit(basis, invalid, Instruction(observation)));
            Assert(session.Memory.Revision == 0 && session.Memory.Facts.IsEmpty);
            session.Commit(basis, new([fact], []), Instruction(observation));
            Fails("coding_run_stale", () => session.Commit(basis, Empty(), Instruction(observation)));
            Assert(session.Memory.Revision == 1 && session.Memory.Attempts.Length == 1);
        });

        Test("clear rejects old run results and releases all task content", () =>
        {
            var session = new CodingSession();
            var observation = session.AddObservation(Input("sensitive source"));
            var old = session.Freeze();
            session.Clear();
            Assert(session.Id != old.SessionId && session.Observations.Count == 0 && session.Memory.Attempts.IsEmpty);
            Fails("coding_run_stale", () => session.Commit(old, Empty(), Instruction(observation)));
        });

        Test("claimed execution and copy do not prove success", () =>
        {
            var session = new CodingSession();
            var observation = session.AddObservation(Input("agent says fixed"));
            var attempt = session.Commit(session.Freeze(), Empty(), Instruction(observation));
            session.MarkCopied(attempt.Id);
            Assert(session.Memory.Attempts[0].Copied && session.Memory.Attempts[0].Assessment is null);
            var basis = session.Freeze();
            var claimed = new AttemptAssessment(attempt.Id, "timeout", "constant", AttemptOutcome.Claimed, true, false, [Ref(observation)]);
            CodingSession.ValidateChanges(basis, new([], [claimed]));
            Fails("coding_execution_unproven", () => CodingSession.ValidateChanges(basis, new([], [claimed with { Outcome = AttemptOutcome.Verified }])));
            Fails("coding_completion_unproven", () => session.Commit(basis, new([], [claimed]), Instruction(observation) with { TaskComplete = true }));
        });

        Test("copying during reasoning preserves the frozen revision and live transfer flag", () =>
        {
            var session = new CodingSession();
            var observation = session.AddObservation(Input("code"));
            var attempt = session.Commit(session.Freeze(), Empty(), Instruction(observation));
            var basis = session.Freeze();
            session.MarkCopied(attempt.Id);
            Assert(session.Memory.Revision == basis.Memory.Revision);
            session.Commit(basis, Empty(), Instruction(observation));
            Assert(session.Memory.Attempts[0].Copied);
        });

        Test("actual outcomes require explicit feedback sources rather than only generic evidence", () =>
        {
            var session = new CodingSession();
            var observation = session.AddObservation(Input("code and test output"));
            var attempt = session.Commit(session.Freeze(), Empty(), Instruction(observation));
            var assessment = new AttemptAssessment(attempt.Id, "timeout", "constant", AttemptOutcome.Unchanged,
                true, true, [Ref(observation)]);
            var basis = session.Freeze();
            Fails("coding_feedback_unproven", () => CodingSession.ValidateChanges(basis, new([], [assessment])));
            Fails("coding_feedback_unproven", () => CodingSession.ValidateChanges(basis, new([], [assessment with { FeedbackObservationIds = [Guid.NewGuid()] }])));
            CodingSession.ValidateChanges(basis, new([], [assessment with { FeedbackObservationIds = [observation.Id] }]));
            Fails("coding_memory_invalid", () => CodingSession.ValidateChanges(basis, new([null!], [])));
            Fails("coding_evidence_invalid", () => CodingSession.ValidateChanges(basis, new([new("x", "fact", "text", [null!])], [])));
        });

        Test("optional unknown feedback safely round trips through the model protocol", () =>
        {
            var assessment = new AttemptAssessment(Guid.NewGuid(), "timeout", "constant", AttemptOutcome.Unknown, false, false, []);
            var serialized = CodingJson.Serialize(assessment);
            using var document = System.Text.Json.JsonDocument.Parse(serialized);
            var restored = CodingJson.Read<AttemptAssessment>(document.RootElement);
            Assert(restored.FeedbackObservationIds.IsDefault && restored.Outcome == AttemptOutcome.Unknown);
        });

        Test("completion requires fresh verification of the latest attempt rather than any historical success", () =>
        {
            var session = new CodingSession();
            var source = session.AddObservation(Input("requirements and code"));
            var first = session.Commit(session.Freeze(), Empty(), Instruction(source));
            var passed = session.AddObservation(Input("Execution 1: sample test passed"));
            var firstVerified = new AttemptAssessment(first.Id, "sample", "fix", AttemptOutcome.Verified,
                true, true, [Ref(passed)], [passed.Id]);
            var latest = session.Commit(session.Freeze(), new([], [firstVerified]), Instruction(source));
            var feedback = session.AddObservation(Input("Execution 2: latest full-suite feedback"));
            var basis = session.Freeze();
            var completion = Instruction(source) with { TaskComplete = true };
            Fails("coding_completion_unproven", () => CodingSession.ValidateChanges(basis, Empty(), completion));
            foreach (var outcome in new[] { AttemptOutcome.Unknown, AttemptOutcome.Unchanged, AttemptOutcome.Regressed })
            {
                var latestNotVerified = new AttemptAssessment(latest.Id, "full-suite", "fix", outcome,
                    true, true, [Ref(feedback)], [feedback.Id]);
                Fails("coding_completion_unproven", () => CodingSession.ValidateChanges(basis, new([], [latestNotVerified]), completion));
            }
            var verified = new AttemptAssessment(latest.Id, "full-suite", "fix", AttemptOutcome.Verified,
                true, true, [Ref(feedback)], [feedback.Id]);
            var alreadyVerified = basis with { Memory = basis.Memory with { Attempts = basis.Memory.Attempts.SetItem(1,
                basis.Memory.Attempts[1] with { Assessment = verified }) } };
            Fails("coding_completion_unproven", () => CodingSession.ValidateChanges(alreadyVerified, Empty(), completion));
            session.Commit(basis, new([], [verified]), completion);
            Assert(session.CurrentDirective!.TaskComplete && session.Memory.Attempts[1].Assessment!.Outcome == AttemptOutcome.Verified);
        });

        Test("source ranges and duplicate updates fail explicitly", () =>
        {
            var session = new CodingSession();
            var observation = session.AddObservation(Input("abcdef"));
            var snapshot = session.Freeze();
            Assert(EvidenceRetriever.Read(snapshot, new(observation.Id, 2, 3)) == "cde");
            foreach (var reference in new[] { new EvidenceRef(observation.Id, -1, 1), new(observation.Id, 0, int.MaxValue), new(observation.Id, 0, 0), new(Guid.NewGuid(), 0, 1) })
                Fails("coding_evidence_invalid", () => EvidenceRetriever.Read(snapshot, reference));
            var fact = new MemoryFact("same", "hypothesis", "possible cause", [Ref(observation)]);
            Fails("coding_memory_invalid", () => CodingSession.ValidateChanges(snapshot, new([fact, fact], [])));
        });

        Test("retrieval returns original offsets and comparisons do not claim freshness", () =>
        {
            var session = new CodingSession();
            var old = session.AddObservation(Input(new string('a', 1250) + " O(n^2) loop"));
            var current = session.AddObservation(Input("O(n log n) sorting"));
            var snapshot = session.Freeze();
            var found = EvidenceRetriever.Search(snapshot, "O(n^2)");
            Assert(found.Count > 0 && found.All(m => EvidenceRetriever.Read(snapshot, m.Reference) == m.Excerpt));
            var comparison = EvidenceRetriever.Compare(snapshot, old.Id, current.Id);
            Assert(!comparison.Identical && comparison.Note.Contains("does not establish"));
        });

        Test("two independent comparable failures trigger but duplicate feedback does not", () =>
        {
            var evidenceA = new EvidenceRef(Guid.NewGuid(), 0, 5);
            var evidenceB = new EvidenceRef(Guid.NewGuid(), 0, 5);
            var first = Assessed(AttemptOutcome.Unchanged, evidenceA);
            var duplicate = Assessed(AttemptOutcome.Unchanged, evidenceA with { Start = 1, Length = 4 });
            Assert(!ProgressAssessment.Evaluate(new(0, [], [first, duplicate])).RequiresDeepAnalysis);
            var second = Assessed(AttemptOutcome.Unchanged, evidenceB);
            var result = ProgressAssessment.Evaluate(new(0, [], [first, second]));
            Assert(result.RequiresDeepAnalysis && result.ComparableFailures == 2);
        });

        Test("independent failures can share code and contract evidence", () =>
        {
            var common = new EvidenceRef(Guid.NewGuid(), 0, 10);
            var first = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 5));
            var second = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 5));
            first = first with { Assessment = first.Assessment! with { Evidence = first.Assessment!.Evidence.Add(common) } };
            second = second with { Assessment = second.Assessment! with { Evidence = second.Assessment!.Evidence.Add(common) } };
            Assert(ProgressAssessment.Evaluate(new(0, [], [first, second])).RequiresDeepAnalysis);
        });

        Test("progress identifies only the unresolved feedback requiring diagnosis", () =>
        {
            var old = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 5));
            var improved = Assessed(AttemptOutcome.Improved, new(Guid.NewGuid(), 0, 5));
            var first = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 5));
            var second = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 5));
            var common = new EvidenceRef(Guid.NewGuid(), 0, 5);
            first = first with { Assessment = first.Assessment! with { Evidence = first.Assessment!.Evidence.Add(common) } };
            var progress = ProgressAssessment.Evaluate(new(0, [], [old, improved, first, second]));
            Assert(progress.RequiresDeepAnalysis && progress.FailureEvidenceIds.Length == 2);
            Assert(progress.FailureEvidenceIds.Contains(first.Assessment!.FeedbackObservationIds[0]));
            Assert(progress.FailureEvidenceIds.Contains(second.Assessment!.FeedbackObservationIds[0]));
            Assert(!progress.FailureEvidenceIds.Contains(common.ObservationId));
            Assert(!progress.FailureEvidenceIds.Contains(old.Assessment!.FeedbackObservationIds[0]));
            var regression = Assessed(AttemptOutcome.Regressed, new(Guid.NewGuid(), 0, 5), "other-strategy");
            var regressed = ProgressAssessment.Evaluate(new(0, [], [first, regression]));
            Assert(regressed.FailureEvidenceIds.SequenceEqual(regression.Assessment!.FeedbackObservationIds));
            var resolved = ProgressAssessment.Evaluate(new(0, [], [first, second, improved]));
            Assert(!resolved.RequiresDeepAnalysis && resolved.FailureEvidenceIds.IsDefaultOrEmpty);
            Assert(CodingJson.Serialize(resolved).Contains("requiresDeepAnalysis"));
        });

        Test("incomparable outcomes and claims are not counted as failed experiments", () =>
        {
            var a = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 1));
            var b = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 1));
            b = b with { Assessment = b.Assessment! with { Comparable = false } };
            var claimed = Assessed(AttemptOutcome.Claimed, new(Guid.NewGuid(), 0, 1));
            Assert(!ProgressAssessment.Evaluate(new(0, [], [a, b, claimed])).RequiresDeepAnalysis);
        });

        Test("improvement clears stale failures for that issue while regression intervenes immediately", () =>
        {
            var a = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 1));
            var b = Assessed(AttemptOutcome.Unchanged, new(Guid.NewGuid(), 0, 1));
            var improvement = Assessed(AttemptOutcome.Improved, new(Guid.NewGuid(), 0, 1), "algorithm-change");
            Assert(!ProgressAssessment.Evaluate(new(0, [], [a, b, improvement])).RequiresDeepAnalysis);
            var regression = Assessed(AttemptOutcome.Regressed, new(Guid.NewGuid(), 0, 1));
            Assert(ProgressAssessment.Evaluate(new(0, [], [regression])).RequiresDeepAnalysis);
        });

        Test("memory and observation bounds fail without dropping existing content", () =>
        {
            var session = new CodingSession();
            for (var i = 0; i < CodingSession.MaximumObservations; i++) session.AddObservation(Input("item " + i));
            Fails("coding_evidence_limit", () => session.AddObservation(Input("overflow")));
            Assert(session.Observations.Count == CodingSession.MaximumObservations);
            Assert(session.AddObservation(Input("item 0")) == session.Observations[0]);
        });

        Test("batch capture is atomic on invalid input or capacity failure", () =>
        {
            var session = new CodingSession();
            Fails("input_empty", () => session.AddObservations([Input("first"), Input(" ")]));
            Assert(session.Observations.Count == 0);
            var batch = session.AddObservations([Input("duplicate"), Input("duplicate"), Input("distinct")]);
            Assert(batch.Length == 3 && batch[0] == batch[1] && session.Observations.Count == 2);
            for (var i = 2; i < CodingSession.MaximumObservations - 1; i++) session.AddObservation(Input("item " + i));
            Fails("coding_evidence_limit", () => session.AddObservations([Input("last"), Input("overflow")]));
            Assert(session.Observations.Count == CodingSession.MaximumObservations - 1);
        });
    }

    private static InputSnapshot Input(string text, uint sequence = 1) => new(Guid.NewGuid(), InputKind.ClipboardText,
        Payload.FromText(text), sequence, DateTimeOffset.UtcNow);
    private static EvidenceRef Ref(Observation observation) => new(observation.Id, 0, observation.Snapshot.Content.Text!.Length);
    private static Directive Instruction(Observation observation) => new("Inspect timeout", "@ file", "Inspect the processing loop.",
        "Return current loop and test output.", "timeout", "constant", "Locate bottleneck", [Ref(observation)]);
    private static MemoryChanges Empty() => new([], []);
    private static ActionAttempt Assessed(AttemptOutcome outcome, EvidenceRef evidence, string strategy = "constant")
    {
        var id = Guid.NewGuid();
        var directive = new Directive("next", "", "next", "results", "timeout", strategy, "improve", [evidence]);
        return new(id, directive, new(id, "timeout", strategy, outcome, true, true, [evidence], [evidence.ObservationId]));
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Coding memory assertion failed"); }
    private static void Fails(string code, Action action)
    {
        try { action(); } catch (SafeFailure failure) { Assert(failure.Code == code); return; }
        throw new Exception("Expected " + code);
    }
}
