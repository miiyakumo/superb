using System.Collections.Immutable;
using System.Text.Json;

namespace Superb.Core.Coding;

// Session and public operations are serialized by the host's event loop, just like Coordinator.
// Network operations never mutate memory; only a validated, current publication commits.
public sealed class CodingAgentRunner(IAgentModelClient client)
{
    private CancellationTokenSource? cancellation;
    private long generation;
    private FrozenRun? retry;
    public bool IsRunning { get; private set; }
    public CodingRunUpdate? Latest { get; private set; }
    public bool CanRetry => !IsRunning && retry is not null && Latest?.State is CodingRunState.Failed or CodingRunState.Cancelled;
    public event Action<CodingRunUpdate>? Updated;

    private sealed record FrozenRun(CodingSession Session, CodingSnapshot Snapshot, Backend Backend,
        CodingRunOptions Options, string Instructions);
    private sealed record SearchArguments(string Query, int Limit = 8);
    private sealed record CompareArguments(Guid Left, Guid Right);
    private sealed record AnalyzeArguments(string Question, ImmutableArray<EvidenceRef> Evidence);
    private sealed record MemoryArguments(string Query = "", int Offset = 0, int Limit = 4,
        ImmutableArray<string> FactIds = default, ImmutableArray<Guid> AttemptIds = default);

    public Task RunAsync(CodingSession session, Backend backend, CodingRunOptions? options = null, string additionalInstructions = "")
    {
        if (IsRunning) throw new SafeFailure("coding_busy");
        var frozen = new FrozenRun(session, session.Freeze(), backend, options ?? new(), additionalInstructions);
        return ExecuteAsync(frozen);
    }

    public Task RetryAsync(CodingSession session)
    {
        if (!CanRetry || retry is not { } frozen || !ReferenceEquals(frozen.Session, session)) throw new SafeFailure("coding_retry_unavailable");
        var current = session.Freeze();
        if (current.SessionId != frozen.Snapshot.SessionId || current.Epoch != frozen.Snapshot.Epoch ||
            current.Memory.Revision != frozen.Snapshot.Memory.Revision) throw new SafeFailure("coding_retry_stale");
        return ExecuteAsync(frozen);
    }

    public void Stop()
    {
        generation++;
        cancellation?.Cancel();
        cancellation = null;
        if (!IsRunning) return;
        IsRunning = false;
        if (Latest is { } prior)
        {
            Latest = prior with { State = CodingRunState.Cancelled, Message = "已停止分析；已提交记忆保留" };
            Updated?.Invoke(Latest);
        }
    }

    public void Clear()
    {
        Stop(); retry = null; Latest = null;
    }

    private async Task ExecuteAsync(FrozenRun run)
    {
        Policy.ValidateBackend(run.Backend);
        var options = run.Options;
        if (!Enum.IsDefined(options.Protocol) || options.MaximumCalls is < 2 or > 40 || options.MaximumSeconds is < 10 or > 1800 ||
            options.MaximumOutputChars is < 1024 or > 1000000 || run.Instructions.Length > 16000)
            throw new SafeFailure("coding_budget_invalid");
        if (run.Snapshot.Observations.IsDefaultOrEmpty) throw new SafeFailure("input_empty");
        var currentGeneration = ++generation;
        var runId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.MaximumSeconds));
        cancellation = cts; retry = run; IsRunning = true;
        var calls = 0; var totalOutput = 0;
        var contextCharacterLimit = Math.Min(100000, Math.Max(2000, run.Backend.MaxInputBytes / 5 - 6000));
        var facts = new Dictionary<string, MemoryFact>(StringComparer.Ordinal);
        var assessments = new Dictionary<Guid, AttemptAssessment>();
        var history = new List<object>();
        var repeated = new Dictionary<string, int>(StringComparer.Ordinal);
        TechnicalAnalysis? analysis = null;
        var priorAnalysisInvalidated = false;
        var activeImage = run.Snapshot.Observations.LastOrDefault(o => o.Snapshot.Content.HasImage);
        string? reviewedPrompt = null;
        var reviewedAccepted = false;
        var repairs = 0;
        bool Current() => generation == currentGeneration && ReferenceEquals(cancellation, cts);
        void CheckCurrent()
        {
            cts.Token.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException(cts.Token);
        }
        void Emit(CodingRunState state, string message, Directive? directive = null)
        {
            if (!Current()) return;
            Latest = new(runId, state, calls, message, directive); Updated?.Invoke(Latest);
        }
        MemoryChanges Changes() => new(facts.Values.ToImmutableArray(), assessments.Values.ToImmutableArray());
        TaskMemory PreviewMemory() => run.Snapshot.Memory with
        {
            Facts = run.Snapshot.Memory.Facts.Where(f => !facts.ContainsKey(f.Id)).Concat(facts.Values).ToImmutableArray(),
            Attempts = run.Snapshot.Memory.Attempts.Select(a => assessments.TryGetValue(a.Id, out var assessment)
                ? a with { Assessment = assessment } : a).ToImmutableArray()
        };
        ProgressDecision Progress() => ProgressAssessment.Evaluate(PreviewMemory());
        bool NeedsAnalysis()
        {
            var progress = Progress();
            if (!progress.RequiresDeepAnalysis || analysis is not null) return false;
            return priorAnalysisInvalidated || progress.FailureEvidenceIds.IsDefaultOrEmpty || !run.Snapshot.Memory.Facts.Any(f =>
                f.Kind == "conclusion" && f.Id.StartsWith("analysis:", StringComparison.Ordinal) &&
                progress.FailureEvidenceIds.All(id => f.Evidence.Any(e => e.ObservationId == id)));
        }
        object CompactMemory()
        {
            var memory = PreviewMemory();
            static string Preview(string value, int max) => value.Length > max ? value[..max] + " [excerpt; retrieve full record]" : value;
            return new
            {
                memory.Revision, factCount = memory.Facts.Length, attemptCount = memory.Attempts.Length,
                facts = memory.Facts.TakeLast(16).Select(f => new { f.Id, f.Kind, text = Preview(f.Text, 500), evidenceCount = f.Evidence.Length }),
                attempts = memory.Attempts.TakeLast(12).Select(a => new
                {
                    a.Id, direction = Preview(a.Directive.Direction, 300), a.Directive.IssueKey, a.Directive.Strategy,
                    a.Copied, assessment = a.Assessment is { } assessment ? new
                    { assessment.Outcome, assessment.Comparable, assessment.ExecutionObserved, assessment.IssueKey, assessment.Strategy } : null
                }),
                retrieval = "read_task_state accepts query, offset, limit (1..8), factIds or attemptIds to retrieve complete records, including older ones."
            };
        }

        string Context(object? focus = null)
        {
            // Complete excerpts and tool results are included or explicitly omitted, never silently cut mid-code.
            var maxChars = contextCharacterLimit;
            var manifest = run.Snapshot.Observations.Take(8).Concat(run.Snapshot.Observations.TakeLast(32)).DistinctBy(o => o.Id).Select(o => new
            {
                o.Id, o.Hash, capturedAt = o.Snapshot.CapturedAt,
                textLength = o.Snapshot.Content.Text?.Length ?? 0, hasImage = o.Snapshot.Content.HasImage,
                version = "unknown; capture order is not file revision"
            });
            var excerpts = new List<object>();
            var used = 0;
            foreach (var o in run.Snapshot.Observations.Reverse().Take(4))
            {
                var text = o.Snapshot.Content.Text ?? "";
                var length = Math.Min(text.Length, Math.Min(10000, Math.Max(0, maxChars / 3 - used)));
                if (length == 0) continue;
                excerpts.Add(new { observationId = o.Id, start = 0, length, text = text[..length], incomplete = length != text.Length });
                used += length;
            }
            var recentHistory = history.ToList();
            string Render() => CodingJson.Serialize(new
            {
                sessionId = run.Snapshot.SessionId, revision = run.Snapshot.Memory.Revision,
                memory = CompactMemory(), observations = manifest, observationCount = run.Snapshot.Observations.Length,
                evidenceRetrieval = "The manifest includes initial and recent observations. search_evidence searches ALL stored originals.", excerpts,
                progress = Progress(), freshAnalysisRequired = NeedsAnalysis(), analysis, focus, toolHistory = recentHistory,
                omittedToolResults = history.Count - recentHistory.Count,
                remainingCalls = options.MaximumCalls - calls,
                userInstructions = run.Instructions
            });
            var result = Render();
            while (result.Length > maxChars && recentHistory.Count > 1) { recentHistory.RemoveAt(0); result = Render(); }
            while (result.Length > maxChars && excerpts.Count > 0) { excerpts.RemoveAt(excerpts.Count - 1); result = Render(); }
            if (result.Length > maxChars) throw new SafeFailure("coding_context_too_large");
            return result;
        }
        async Task<AgentAction> Call(string system, object? focus = null, IEnumerable<EvidenceRef>? visualEvidence = null)
        {
            CheckCurrent();
            if (calls >= options.MaximumCalls) throw new SafeFailure("coding_call_budget_exhausted");
            var content = Payload.FromText(Context(focus));
            var visualIds = visualEvidence?.Select(e => e.ObservationId).ToHashSet();
            var imageObservations = visualIds is not null
                ? run.Snapshot.Observations.Where(o => visualIds.Contains(o.Id) && o.Snapshot.Content.HasImage).ToArray()
                : activeImage is not null ? new[] { activeImage } : [];
            if (imageObservations.Length > 0)
            {
                var images = imageObservations.SelectMany(o => o.Snapshot.Content.Images).ToArray();
                content = content with { Text = content.Text + "\nAttached image packets in order: " +
                    CodingJson.Serialize(imageObservations.Select(o => new { observationId = o.Id, count = o.Snapshot.Content.Images.Count() })),
                    Png = images[0].Png, Width = images[0].Width, Height = images[0].Height, AdditionalImages = images.Skip(1).ToImmutableArray() };
            }
            calls++;
            Emit(Latest?.State ?? CodingRunState.Analyzing, $"后端分析 · 第 {calls}/{options.MaximumCalls} 次调用");
            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            callCts.CancelAfter(TimeSpan.FromSeconds(run.Backend.TimeoutSeconds));
            var action = await client.NextAsync(new(run.Backend, options.Protocol, system, content), callCts.Token);
            CheckCurrent();
            totalOutput += CodingJson.Serialize(action).Length;
            if (totalOutput > options.MaximumOutputChars) throw new SafeFailure("coding_output_budget_exhausted");
            return action;
        }
        void ValidateEvidence(ImmutableArray<EvidenceRef> evidence)
        {
            if (evidence.IsDefaultOrEmpty) throw new SafeFailure("coding_evidence_required");
            foreach (var reference in evidence) _ = EvidenceRetriever.Read(run.Snapshot, reference);
        }
        async Task<SolutionReview> Review(Directive directive)
        {
            Emit(CodingRunState.Reviewing, "检查修改方案与证据");
            var response = await Call(CodingPrompts.Reviewer, new
            {
                directive,
                sources = directive.Evidence.Select(e => new { reference = e, text = EvidenceRetriever.Read(run.Snapshot, e) })
            }, directive.Evidence);
            if (response.Name != "review_result") throw new SafeFailure("coding_review_invalid");
            var result = CodingJson.Read<SolutionReview>(response.Arguments);
            ValidateEvidence(result.Evidence);
            if (string.IsNullOrWhiteSpace(result.Findings)) throw new SafeFailure("coding_review_invalid");
            reviewedPrompt = CodingJson.Serialize(directive); reviewedAccepted = result.Accepted;
            history.Add(new { tool = "review_solution", result });
            return result;
        }

        try
        {
            Emit(CodingRunState.Analyzing, "读取任务记忆并评估新反馈");
            while (true)
            {
                CheckCurrent();
                AgentAction action;
                try { action = await Call(CodingPrompts.Controller); }
                catch (SafeFailure ex) when (ex.Code is "coding_action_invalid" or "coding_response_invalid" && repairs++ < 2)
                {
                    history.Add(new { error = "coding_action_invalid", detail = AgentModelClient.ProtocolFailureDetail(ex),
                        instruction = "Return exactly {\"name\":\"<documented action name>\",\"arguments\":{...}}. Use name, not action or tool. Arguments must be an object. No fences or extra fields. Choose the next useful action from the system contract." });
                    continue;
                }
                var signature = CodingJson.Serialize(action);
                repeated.TryGetValue(signature, out var count); repeated[signature] = count + 1;
                if (count >= 2) throw new SafeFailure("coding_internal_loop");
                try
                {
                    object result;
                    switch (action.Name)
                    {
                        case "read_task_state":
                            var selection = CodingJson.Read<MemoryArguments>(action.Arguments);
                            if (selection.Query is null || selection.Query.Length > 2000 || selection.Offset < 0 || selection.Limit is < 1 or > 8 ||
                                (!selection.FactIds.IsDefault && selection.FactIds.Length > 8) || (!selection.AttemptIds.IsDefault && selection.AttemptIds.Length > 8))
                                throw new SafeFailure("coding_action_invalid");
                            var memory = PreviewMemory();
                            var matchingFacts = memory.Facts.Where(f => (selection.FactIds.IsDefaultOrEmpty || selection.FactIds.Contains(f.Id)) &&
                                (selection.Query.Length == 0 || (f.Id + " " + f.Kind + " " + f.Text).Contains(selection.Query, StringComparison.OrdinalIgnoreCase))).ToArray();
                            var matchingAttempts = memory.Attempts.Where(a => (selection.AttemptIds.IsDefaultOrEmpty || selection.AttemptIds.Contains(a.Id)) &&
                                (selection.Query.Length == 0 || CodingJson.Serialize(a).Contains(selection.Query, StringComparison.OrdinalIgnoreCase))).ToArray();
                            result = new
                            {
                                memory.Revision, factMatches = matchingFacts.Length, attemptMatches = matchingAttempts.Length,
                                facts = matchingFacts.Skip(selection.Offset).Take(selection.Limit),
                                attempts = matchingAttempts.Skip(selection.Offset).Take(selection.Limit), progress = Progress()
                            }; break;
                        case "search_evidence":
                            Emit(CodingRunState.Retrieving, "检索题目、代码和历史反馈");
                            var search = CodingJson.Read<SearchArguments>(action.Arguments);
                            result = EvidenceRetriever.Search(run.Snapshot, search.Query, search.Limit); break;
                        case "read_evidence":
                            Emit(CodingRunState.Retrieving, "读取相关原始材料");
                            var reference = CodingJson.Read<EvidenceRef>(action.Arguments);
                            if (reference.Length > 32000) throw new SafeFailure("coding_evidence_read_too_large");
                            var evidenceText = EvidenceRetriever.Read(run.Snapshot, reference);
                            var selected = run.Snapshot.Observations.First(o => o.Id == reference.ObservationId);
                            if (selected.Snapshot.Content.HasImage) activeImage = selected;
                            result = new { reference, text = evidenceText, imageAttachedOnNextCall = selected.Snapshot.Content.HasImage }; break;
                        case "compare_observations":
                            var comparison = CodingJson.Read<CompareArguments>(action.Arguments);
                            result = EvidenceRetriever.Compare(run.Snapshot, comparison.Left, comparison.Right); break;
                        case "stage_memory_changes":
                            var changes = CodingJson.Read<MemoryChanges>(action.Arguments);
                            // An omitted collection is an empty patch, never deletion of existing memory.
                            changes = changes with { Facts = changes.Facts.IsDefault ? [] : changes.Facts,
                                Assessments = changes.Assessments.IsDefault ? [] : changes.Assessments };
                            CodingSession.ValidateChanges(run.Snapshot, changes);
                            if (changes.Facts.Any(f => f.Id.StartsWith("analysis:", StringComparison.Ordinal)))
                            {
                                result = new { staged = false, error = "coding_memory_reserved_id",
                                    instruction = "The analysis: ID namespace is reserved for analyze_issue results. Use another fact ID for a correction, then call analyze_issue when needed. Do not create or overwrite these records." };
                                break;
                            }
                            var before = PreviewMemory();
                            var changedFacts = changes.Facts.Any(f => !before.Facts.Any(old => old.Id == f.Id && CodingJson.Serialize(old) == CodingJson.Serialize(f)));
                            var changed = changedFacts ||
                                changes.Assessments.Any(a => !before.Attempts.Any(old => old.Id == a.AttemptId && CodingJson.Serialize(old.Assessment) == CodingJson.Serialize(a)));
                            var nextFacts = new Dictionary<string, MemoryFact>(facts, StringComparer.Ordinal);
                            var nextAssessments = new Dictionary<Guid, AttemptAssessment>(assessments);
                            if (changed) nextFacts.Remove("analysis:" + runId.ToString("N"));
                            foreach (var fact in changes.Facts) nextFacts[fact.Id] = fact;
                            foreach (var assessment in changes.Assessments) nextAssessments[assessment.AttemptId] = assessment;
                            CodingSession.ValidateChanges(run.Snapshot, new(nextFacts.Values.ToImmutableArray(), nextAssessments.Values.ToImmutableArray()));
                            if (changed)
                            {
                                analysis = null; reviewedPrompt = null;
                                priorAnalysisInvalidated |= changedFacts;
                            }
                            facts.Clear(); assessments.Clear();
                            foreach (var fact in nextFacts) facts[fact.Key] = fact.Value;
                            foreach (var assessment in nextAssessments) assessments[assessment.Key] = assessment.Value;
                            result = new { staged = true, progress = Progress(), freshAnalysisRequired = NeedsAnalysis() }; break;
                        case "assess_progress":
                            RequireEmpty(action.Arguments); result = Progress(); break;
                        case "analyze_issue":
                            if (run.Snapshot.Memory.Attempts.LastOrDefault() is { } previousAttempt && !assessments.ContainsKey(previousAttempt.Id))
                            {
                                result = new { analyzed = false, reason = "First stage your assessment of the latest attempt (Unknown if unclear), plus any contract corrections, then analyze. The analysis result is saved automatically.", attemptId = previousAttempt.Id }; break;
                            }
                            var question = CodingJson.Read<AnalyzeArguments>(action.Arguments);
                            if (string.IsNullOrWhiteSpace(question.Question)) throw new SafeFailure("coding_action_invalid");
                            ValidateEvidence(question.Evidence);
                            Emit(CodingRunState.Diagnosing, "后端正在深入分析瓶颈与替代方案");
                            var answer = await Call(CodingPrompts.Analyst, new
                            {
                                question.Question,
                                sources = question.Evidence.Select(e => new { reference = e, text = EvidenceRetriever.Read(run.Snapshot, e) })
                            }, question.Evidence);
                            if (answer.Name != "analysis_result") throw new SafeFailure("coding_analysis_invalid");
                            var candidateAnalysis = CodingJson.Read<TechnicalAnalysis>(answer.Arguments);
                            ValidateEvidence(candidateAnalysis.Evidence);
                            if (new[] { candidateAnalysis.Conclusion, candidateAnalysis.ProposedChange, candidateAnalysis.Correctness, candidateAnalysis.Complexity, candidateAnalysis.Checks }.Any(string.IsNullOrWhiteSpace) ||
                                CodingJson.Serialize(candidateAnalysis).Length > contextCharacterLimit / 2)
                                throw new SafeFailure("coding_analysis_invalid");
                            var factId = "analysis:" + runId.ToString("N");
                            var retainedAnalysis = $"{candidateAnalysis.Conclusion}\n正确性：{candidateAnalysis.Correctness}\n复杂度：{candidateAnalysis.Complexity}\n验证：{candidateAnalysis.Checks}";
                            if (retainedAnalysis.Length > 12000) throw new SafeFailure("coding_analysis_invalid");
                            analysis = candidateAnalysis;
                            facts[factId] = new(factId, "conclusion", retainedAnalysis, analysis.Evidence.Concat(question.Evidence).Distinct().ToImmutableArray());
                            reviewedPrompt = null;
                            result = analysis; break;
                        case "review_solution":
                            var candidate = CodingDirectivePolicy.Prepare(CodingJson.Read<Directive>(action.Arguments));
                            CodingSession.ValidateChanges(run.Snapshot, Changes(), candidate);
                            result = await Review(candidate); break;
                        case "publish_directive":
                            var directive = CodingDirectivePolicy.Prepare(CodingJson.Read<Directive>(action.Arguments));
                            CodingSession.ValidateChanges(run.Snapshot, Changes(), directive);
                            if (run.Snapshot.Memory.Attempts.LastOrDefault() is { } latestAttempt && !assessments.ContainsKey(latestAttempt.Id))
                            {
                                result = new { published = false, reason = "Assess the prior attempt (latest directive) against available feedback using stage_memory_changes first. Use Unknown/Claimed/Incomparable if execution or comparison is unproven.", attemptId = latestAttempt.Id }; break;
                            }
                            if (PreviewMemory().Facts.IsEmpty)
                            {
                                result = new { published = false, reason = "Persist the grounded task constraints or missing-information issue using stage_memory_changes before publishing." }; break;
                            }
                            if (NeedsAnalysis())
                            {
                                result = new { published = false, reason = "Repeated comparable failure requires analyze_issue before another directive." }; break;
                            }
                            if (reviewedPrompt != CodingJson.Serialize(directive)) await Review(directive);
                            if (!reviewedAccepted)
                            {
                                result = new { published = false, reason = "Review rejected the directive; revise it using the review findings." }; break;
                            }
                            CheckCurrent();
                            run.Session.Commit(run.Snapshot, Changes(), directive);
                            Emit(CodingRunState.AwaitingFeedback, directive.TaskComplete ? "验收证据已记录" : "指令已就绪 · 等待平台反馈", directive);
                            retry = null;
                            return;
                        default: throw new SafeFailure("coding_action_unknown");
                    }
                    if (CodingJson.Serialize(result).Length > contextCharacterLimit / 2)
                        result = new { error = "coding_tool_result_too_large", instruction = "The result exceeds the context budget. Request a narrower evidence range, query or smaller page; the original remains available." };
                    history.Add(new { tool = action.Name, result });
                    Emit(CodingRunState.Analyzing, "根据证据选择下一步");
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException || ex is SafeFailure failure && IsRepairable(failure.Code))
                {
                    if (++repairs > 2) throw new SafeFailure("coding_action_repair_exhausted");
                    history.Add(new { tool = action.Name, error = ex is SafeFailure safe ? safe.Code : "coding_action_invalid",
                        field = ex is JsonException json ? json.Path : null,
                        instruction = "Correct the action using actual IDs, schema field types and valid evidence ranges. Narrative fields are strings; checks may also be an array of strings. Empty memory patch collections may be omitted. If too large, request a narrower source span or shorter candidate. Nothing was committed." });
                }
            }
        }
        catch (OperationCanceledException)
        {
            Emit(CodingRunState.Failed, cts.IsCancellationRequested ? "coding_time_budget_exhausted" : "request_timed_out");
        }
        catch (SafeFailure ex) { Emit(CodingRunState.Failed, ex.Code); }
        catch (Exception ex) { Emit(CodingRunState.Failed, ConnectionDiagnostics.CodeFor(ex)); }
        finally
        {
            facts.Clear(); assessments.Clear(); history.Clear(); repeated.Clear(); analysis = null;
            if (Current()) { IsRunning = false; cancellation = null; }
        }
    }
    private static bool IsRepairable(string code) => code.StartsWith("coding_evidence", StringComparison.Ordinal) ||
        code is "coding_action_invalid" or "coding_action_unknown" or "coding_directive_invalid" or "coding_memory_invalid" or
        "coding_assessment_invalid" or "coding_analysis_invalid" or "coding_review_invalid" or "coding_search_invalid" or
        "coding_execution_unproven" or "coding_completion_unproven" or "coding_feedback_unproven" or "coding_context_too_large";
    private static void RequireEmpty(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any()) throw new SafeFailure("coding_action_invalid");
    }
}
