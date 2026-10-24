using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;

namespace Superb.Core.Coding;

// Session and public operations are serialized by the host's event loop, just like Coordinator.
// Network operations never mutate memory; only a validated, current publication commits.
public sealed class CodingAgentRunner(IAgentModelClient client)
{
    private CancellationTokenSource? cancellation;
    private long generation;
    private FrozenRun? retry;
    private CompletedConversation? completedConversation;
    public bool IsRunning { get; private set; }
    public CodingRunUpdate? Latest { get; private set; }
    public bool CanRetry => !IsRunning && retry is not null && Latest?.State is CodingRunState.Failed or CodingRunState.Cancelled or CodingRunState.Paused;
    public event Action<CodingRunUpdate>? Updated;
    public event Action<CodingDiagnosticEvent>? Diagnostic;
    private RunTelemetry? telemetry;
    private sealed class RunTelemetry(Guid id)
    {
        public Guid Id { get; } = id;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public long Sequence, CallStartedAt;
        public bool Waiting;
        public CodingTransportSnapshot? Transport;
        public CodingStepMetrics? Step;
    }
    private void Publish(RunTelemetry run, CodingRunState state, int calls, string message, CodingDiagnosticKind kind,
        Directive? directive = null, string? errorCode = null, CodingTool? tool = null, long? callElapsed = null)
    {
        var elapsed = run.Clock.ElapsedMilliseconds;
        var code = CodingDiagnostics.SafeCode(errorCode);
        Latest = new(run.Id, state, calls, message, directive, elapsed, run.Waiting,
            run.Waiting ? elapsed - run.CallStartedAt : callElapsed ?? 0, code, run.Transport);
        var diagnostic = new CodingDiagnosticEvent(run.Id, ++run.Sequence, DateTimeOffset.UtcNow, kind, state,
            calls, elapsed, callElapsed, tool, code, run.Transport, run.Step);
        // Diagnostics must never fail or cancel the user's analysis.
        try { Diagnostic?.Invoke(diagnostic); } catch { }
        try { Updated?.Invoke(Latest); } catch { }
    }

    private sealed record CompletedConversation(Guid SessionId, long Epoch, long Revision, Backend Backend,
        ImmutableArray<AgentMessage> Messages);
    private sealed class FrozenRun(CodingSession session, CodingSnapshot snapshot, Backend backend, CodingRunOptions options, string instructions)
    {
        public CodingSession Session { get; } = session;
        public CodingSnapshot Snapshot { get; } = snapshot;
        public Backend Backend { get; } = backend;
        public CodingRunOptions Options { get; } = options;
        public string Instructions { get; } = instructions;
        public List<AgentMessage> Messages { get; } = [];
        public Dictionary<string, MaterialPacket> Cache { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, List<(int Start, int End)>> Coverage { get; } = [];
        public HashSet<string> CallIds { get; } = new(StringComparer.Ordinal);
        public int NoProgress, Repairs;
        public AgentAction? PendingAction;
        public bool Initialized;
        public bool Cover(EvidenceRef reference)
        {
            if (!Coverage.TryGetValue(reference.ObservationId, out var ranges)) Coverage[reference.ObservationId] = ranges = [];
            var end = reference.Start + reference.Length;
            if (ranges.Any(r => r.Start <= reference.Start && r.End >= end)) return false;
            ranges.Add((reference.Start, end));
            ranges.Sort((a,b) => a.Start.CompareTo(b.Start));
            for (var i = ranges.Count - 1; i > 0; i--)
                if (ranges[i - 1].End >= ranges[i].Start)
                { ranges[i - 1] = (ranges[i - 1].Start, Math.Max(ranges[i - 1].End, ranges[i].End)); ranges.RemoveAt(i); }
            return true;
        }
    }
    public Task RunAsync(CodingSession session, Backend backend, CodingRunOptions? options = null, string additionalInstructions = "")
    {
        if (IsRunning) throw new SafeFailure("coding_busy");
        return ExecuteAsync(new(session, session.Freeze(), backend, options ?? new(), additionalInstructions), false);
    }
    public Task RetryAsync(CodingSession session)
    {
        if (!CanRetry || retry is not { } frozen || !ReferenceEquals(frozen.Session, session)) throw new SafeFailure("coding_retry_unavailable");
        var current = session.Freeze();
        if (current.SessionId != frozen.Snapshot.SessionId || current.Epoch != frozen.Snapshot.Epoch || current.Memory.Revision != frozen.Snapshot.Memory.Revision)
            throw new SafeFailure("coding_retry_stale");
        return ExecuteAsync(frozen, true);
    }
    public void Stop()
    {
        generation++; cancellation?.Cancel(); cancellation = null;
        if (!IsRunning) return;
        IsRunning = false;
        if (Latest is { } prior && telemetry is { } run)
        {
            run.Waiting = false; run.Clock.Stop();
            Publish(run, CodingRunState.Cancelled, prior.Calls, "已停止分析；完整工具步骤已保留，可从检查点继续", CodingDiagnosticKind.Cancelled, errorCode: "request_cancelled");
        }
        telemetry = null;
    }
    public void Clear() { Stop(); retry = null; completedConversation = null; Latest = null; }

    private void Initialize(FrozenRun run)
    {
        if (run.Initialized) return;
        if (completedConversation is { } prior && prior.SessionId == run.Snapshot.SessionId && prior.Epoch == run.Snapshot.Epoch &&
            prior.Revision == run.Snapshot.Memory.Revision && prior.Backend == run.Backend)
        {
            run.Messages.AddRange(prior.Messages);
            foreach (var message in prior.Messages) if (message.Call is { } call) run.CallIds.Add(call.CallId);
        }
        var excerpts = run.Snapshot.Observations.TakeLast(4).Select(o =>
        {
            var text = o.Snapshot.Content.Text ?? "";
            var length = Math.Min(text.Length, 8000);
            var reference = new EvidenceRef(o.Id, 0, length);
            if (length > 0 || o.Snapshot.Content.HasImage) run.Cover(reference);
            return new { reference, text = text[..length], incomplete = length != text.Length };
        }).ToArray();
        var decision = ProgressAssessment.Evaluate(run.Snapshot.Memory);
        var image = run.Backend.SupportsImages ? run.Snapshot.Observations.LastOrDefault(o => o.Snapshot.Content.HasImage) : null;
        var context = CodingJson.Serialize(new
        {
            sessionId = run.Snapshot.SessionId, memory = run.Snapshot.Memory,
            observations = run.Snapshot.Observations.Select(o => new { o.Id, textLength = o.Snapshot.Content.Text?.Length ?? 0,
                hasImage = o.Snapshot.Content.HasImage, capturedAt = o.Snapshot.CapturedAt }), excerpts,
            progress = new { reconsiderStrategy = decision.RequiresDeepAnalysis, decision.Reason, decision.ComparableFailures },
            callLimit = run.Options.MaximumCalls, totalSeconds = run.Options.MaximumSeconds,
            userInstructions = run.Instructions,
            attachedImageObservationId = image?.Id,
            instruction = "Use the supplied state and material directly. Retrieve only missing original passages. submit_result is valid immediately; missing source is a reason to request focused material, not to invent a repair."
        });
        run.Messages.Add(new(AgentRole.User, image is null ? Payload.FromText(context) : image.Snapshot.Content with { Text = context }));
        run.Initialized = true;
    }
    private static ImmutableArray<AgentMessage> ProjectMessages(FrozenRun run)
    {
        // Keep every native call/output pair. Only image attachments from earlier packets are
        // replaced by an explicit notice; originals remain retrievable in the frozen snapshot.
        var lastImage = run.Messages.FindLastIndex(m => m.Content.HasImage);
        var result = run.Messages.Select((m,i) => i != lastImage && m.Content.HasImage
            ? m with { Content = Payload.FromText(m.Content.Text + "\nEarlier image attachment omitted; retrieve_material can attach its original again.") } : m).ToImmutableArray();
        var chars = result.Sum(m => (long)(m.Content.Text?.Length ?? 0) + (m.Call?.Arguments.Length ?? 0) + (m.Call?.ReasoningContent?.Length ?? 0));
        if (chars > Math.Min(1000000, run.Backend.MaxInputBytes / 3)) throw new SafeFailure("coding_context_too_large");
        return result;
    }
    private async Task ExecuteAsync(FrozenRun run, bool resumed)
    {
        Policy.ValidateBackend(run.Backend); run.Options.Validate();
        if (run.Instructions.Length > 16000) throw new SafeFailure("coding_budget_invalid");
        if (run.Snapshot.Observations.IsDefaultOrEmpty) throw new SafeFailure("input_empty");
        var currentGeneration = ++generation;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(run.Options.MaximumSeconds));
        cancellation = cts; retry = run; IsRunning = true;
        var runTelemetry = new RunTelemetry(Guid.NewGuid()); telemetry = runTelemetry;
        var calls = 0; var totalOutput = 0;
        bool Current() => generation == currentGeneration && ReferenceEquals(cancellation, cts);
        void CheckCurrent() { cts.Token.ThrowIfCancellationRequested(); if (!Current()) throw new OperationCanceledException(cts.Token); }
        void Emit(CodingRunState state, string message, CodingDiagnosticKind kind = CodingDiagnosticKind.Phase,
            string? code = null, CodingTool? tool = null, long? callElapsed = null, Directive? directive = null)
        { if (Current()) Publish(runTelemetry, state, calls, message, kind, directive, code, tool, callElapsed); }
        void Answer(AgentAction action, object value)
        {
            run.Messages.Add(new(AgentRole.Tool, Payload.FromText(CodingJson.Serialize(value)), ToolCallId: action.CallId));
            run.PendingAction = null;
        }
        try
        {
            Initialize(run);
            if (resumed)
            {
                run.NoProgress = 0; run.Repairs = 0;
            }
            Emit(CodingRunState.Analyzing, resumed ? "从上次完整工具步骤继续；冻结材料和会话已恢复" : "结合任务状态和现有材料直接分析", CodingDiagnosticKind.Started);
            // The checkpoint is already balanced. Resume the pending model step, not old tools.
            if (resumed) Emit(CodingRunState.Analyzing, "已恢复完整工具对话检查点", CodingDiagnosticKind.Resumed);
            while (true)
            {
                CheckCurrent();
                if (run.Options.MaximumCalls is { } limit && calls >= limit) throw new SafeFailure("coding_call_budget_exhausted");
                var messages = ProjectMessages(run);
                runTelemetry.Step = new(messages.Length, messages.Sum(m => (long)(m.Content.Text?.Length ?? 0) + (m.Call?.Arguments.Length ?? 0) + (m.Call?.ReasoningContent?.Length ?? 0)),
                    NoProgressSteps: run.NoProgress, CheckpointResumed: resumed);
                runTelemetry.Transport = null; runTelemetry.Waiting = true; runTelemetry.CallStartedAt = runTelemetry.Clock.ElapsedMilliseconds;
                calls++;
                Emit(CodingRunState.Analyzing, $"等待模型响应 · 第 {calls} 次调用", CodingDiagnosticKind.CallStarted);
                using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                callCts.CancelAfter(TimeSpan.FromSeconds(run.Backend.TimeoutSeconds));
                var trace = new CodingTransportTrace();
                AgentAction action;
                try
                {
                    var pending = client.NextAsync(new(run.Backend, CodingPrompts.Controller, messages, trace), callCts.Token);
                    CodingTransportStage? previousStage = null; long lastProgress = 0;
                    while (!pending.IsCompleted && !callCts.IsCancellationRequested)
                    {
                        await Task.WhenAny(pending, Task.Delay(1000, callCts.Token));
                        if (pending.IsCompleted || callCts.IsCancellationRequested) break;
                        CheckCurrent(); if (!trace.Started) continue;
                        var progress = trace.Snapshot;
                        if (progress.Stage != previousStage || progress.ElapsedMilliseconds - lastProgress >= 5000)
                        {
                            runTelemetry.Transport = progress;
                            Emit(CodingRunState.Analyzing, CodingTransportTrace.Describe(progress), CodingDiagnosticKind.Transport);
                            previousStage = progress.Stage; lastProgress = progress.ElapsedMilliseconds;
                        }
                    }
                    action = await pending.WaitAsync(callCts.Token);
                    CheckCurrent();
                    runTelemetry.Transport = trace.Started ? trace.Snapshot : null; runTelemetry.Waiting = false;
                    Emit(CodingRunState.Analyzing, "已收到工具调用：" + CodingDiagnostics.ToolLabel(CodingDiagnostics.Tool(action.Name)),
                        CodingDiagnosticKind.CallCompleted, tool: CodingDiagnostics.Tool(action.Name), callElapsed: runTelemetry.Clock.ElapsedMilliseconds - runTelemetry.CallStartedAt);
                }
                catch (Exception ex)
                {
                    runTelemetry.Waiting = false; runTelemetry.Transport = trace.Started ? trace.Snapshot : null;
                    Emit(CodingRunState.Analyzing, "本次模型请求未完成；检查点保留", CodingDiagnosticKind.CallFailed,
                        cts.IsCancellationRequested ? "coding_time_budget_exhausted" : ex is OperationCanceledException ? "request_timed_out" : ConnectionDiagnostics.CodeFor(ex),
                        callElapsed: runTelemetry.Clock.ElapsedMilliseconds - runTelemetry.CallStartedAt);
                    if (ex is SafeFailure format && (format.Code is "coding_action_invalid" or "coding_tool_call_required"))
                    {
                        if (++run.Repairs >= 4) throw new SafeFailure("coding_action_repair_exhausted");
                        // No complete native call was accepted, so no call ID or tool result is invented.
                        run.Messages.Add(new(AgentRole.User, Payload.FromText("Continue this task with one native retrieve_material or submit_result call. Final instructions must be submitted with submit_result. Do not return a text action envelope.")));
                        Emit(CodingRunState.Analyzing, "已要求模型修正原生工具调用格式，原会话继续", CodingDiagnosticKind.Repair, format.Code);
                        continue;
                    }
                    throw;
                }
                if (string.IsNullOrWhiteSpace(action.CallId) || action.CallId.Length > 256 || action.CallId.Any(char.IsControl) || !run.CallIds.Add(action.CallId))
                    throw new SafeFailure("coding_history_invalid");
                run.Messages.Add(new(AgentRole.Assistant, Payload.FromText(action.Commentary ?? ""), action));
                run.PendingAction = action;
                if (!string.IsNullOrWhiteSpace(action.Commentary))
                    Emit(CodingRunState.Analyzing, "模型进度：" + action.Commentary[..Math.Min(600, action.Commentary.Length)]);
                totalOutput += action.Arguments.Length + (action.Commentary?.Length ?? 0);
                if (totalOutput > run.Options.MaximumOutputChars)
                { Answer(action, new { error = "coding_output_budget_exhausted" }); throw new SafeFailure("coding_output_budget_exhausted"); }
                var terminal = false;
                try
                {
                    switch (action.Name)
                    {
                        case "retrieve_material":
                            var query = CodingJson.Read<MaterialQuery>(action.Arguments);
                            var normalized = query with { Query = query.Query?.Trim()!, Ranges = query.Ranges.IsDefault ? [] : query.Ranges };
                            var key = CodingJson.Serialize(normalized);
                            var cached = run.Cache.TryGetValue(key, out var packet);
                            packet ??= CodingMaterialStore.Retrieve(run.Snapshot, normalized);
                            var imageIds = packet.Passages.Where(p => p.HasImage).Select(p => p.Reference.ObservationId).ToHashSet();
                            var images = run.Snapshot.Observations.Where(o => imageIds.Contains(o.Id)).ToArray();
                            var payloads = images.SelectMany(o => o.Snapshot.Content.Images).ToArray();
                            if (payloads.Length > 0 && !run.Backend.SupportsImages) throw new SafeFailure("backend_has_no_vision");
                            if (payloads.Length > Policy.MaximumImages) throw new SafeFailure("coding_material_query_invalid");
                            run.Cache[key] = packet;
                            var fresh = packet.Passages.Count(p => run.Cover(p.Reference));
                            run.NoProgress = fresh > 0 ? 0 : run.NoProgress + 1;
                            Answer(action, new { packet.Passages, packet.Matches, packet.More, cached, newPassages = fresh,
                                guidance = run.NoProgress >= 3 ? "No new evidence was obtained. Use the returned material to submit_result, or request specific missing source from the platform through submit_result. Repeated reads do not establish progress." : "Use these originals directly; no extra state or review tools are needed." });
                            if (images.Length > 0)
                            {
                                run.Messages.Add(new(AgentRole.User, new Payload("Retrieved image originals: " + CodingJson.Serialize(images.Select(o => o.Id)),
                                    payloads[0].Png, payloads[0].Width, payloads[0].Height, payloads.Skip(1).ToImmutableArray())));
                            }
                            runTelemetry.Step = runTelemetry.Step with { RetrievedPassages = packet.Passages.Length, Cached = cached, NoProgressSteps = run.NoProgress };
                            Emit(CodingRunState.Retrieving, cached ? "已复用原始材料，继续形成执行指令" : $"已取回 {packet.Passages.Length} 段原始材料", CodingDiagnosticKind.ToolCompleted, tool: CodingTool.RetrieveMaterial);
                            if (run.NoProgress >= 6) throw new SafeFailure("coding_no_progress");
                            break;
                        case "submit_result":
                            var submission = CodingJson.Read<CodingSubmission>(action.Arguments);
                            if (submission.Directive is null) throw new SafeFailure("coding_directive_invalid");
                            var changes = submission.Memory ?? new MemoryChanges([], []);
                            changes = changes with { Facts = changes.Facts.IsDefault ? [] : changes.Facts, Assessments = changes.Assessments.IsDefault ? [] : changes.Assessments };
                            var directive = CodingDirectivePolicy.Prepare(submission.Directive);
                            CodingSession.ValidateChanges(run.Snapshot, changes, directive);
                            CheckCurrent();
                            var attempt = run.Session.Commit(run.Snapshot, changes, directive);
                            Answer(action, new { submitted = true, attemptId = attempt.Id });
                            completedConversation = new(run.Snapshot.SessionId, run.Snapshot.Epoch, run.Session.Memory.Revision, run.Backend, run.Messages.ToImmutableArray());
                            retry = null; terminal = true;
                            Emit(CodingRunState.AwaitingFeedback, directive.TaskComplete ? "验收证据已记录" : "执行指令已提交 · 等待平台反馈", CodingDiagnosticKind.Completed,
                                tool: CodingTool.SubmitResult, directive: directive);
                            return;
                        default: throw new SafeFailure("coding_action_unknown");
                    }
                }
                catch (Exception ex) when (!terminal && (ex is JsonException or ArgumentException || ex is SafeFailure safe && IsRepairable(safe.Code)))
                {
                    var code = ex is SafeFailure failure ? failure.Code : "coding_action_invalid";
                    Answer(action, new { error = code, instruction = "Correct the arguments or evidence using the two provided tool schemas. Nothing was submitted. Missing source can be requested with submit_result." });
                    Emit(CodingRunState.Analyzing, "工具结果未通过校验，已将原因返回模型修正", CodingDiagnosticKind.Repair, code, CodingDiagnostics.Tool(action.Name));
                    if (++run.Repairs >= 4) throw new SafeFailure("coding_action_repair_exhausted");
                }
            }
        }
        catch (OperationCanceledException)
        { var code = cts.IsCancellationRequested ? "coding_time_budget_exhausted" : "request_timed_out"; Emit(CodingRunState.Failed, code, CodingDiagnosticKind.Failed, code); }
        catch (SafeFailure ex)
        { Emit(ex.Code == "coding_no_progress" ? CodingRunState.Paused : CodingRunState.Failed, CodingDiagnostics.SafeCode(ex.Code)!,
            ex.Code == "coding_no_progress" ? CodingDiagnosticKind.Paused : CodingDiagnosticKind.Failed, ex.Code); }
        catch (Exception ex)
        { var code = ConnectionDiagnostics.CodeFor(ex); Emit(CodingRunState.Failed, code, CodingDiagnosticKind.Failed, code); }
        finally
        {
            if (run.PendingAction is { } interrupted)
                Answer(interrupted, new { error = "request_cancelled", instruction = "Tool execution was interrupted before a result was accepted. Continue from the retained task state." });
            runTelemetry.Waiting = false; runTelemetry.Clock.Stop();
            if (Current()) { IsRunning = false; cancellation = null; telemetry = null; }
        }
    }
    private static bool IsRepairable(string code) => code is "coding_action_invalid" or "coding_action_unknown" or "coding_directive_invalid" or
        "coding_memory_invalid" or "coding_memory_limit" or "coding_assessment_invalid" or "coding_material_query_invalid" or "coding_evidence_invalid" or
        "coding_evidence_read_too_large" or "backend_has_no_vision" or "coding_execution_unproven" or "coding_completion_unproven" or "coding_feedback_unproven";
}
