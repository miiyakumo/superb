using System.Collections.Immutable;
using System.Text.Json;
using Superb.Core;
using Superb.Core.Coding;

internal static class CodingRunnerTests
{
    private static readonly Backend Backend = new("coding", "synthetic", "https://example.test/v1/chat/completions", "test", "");
    internal static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Func<Task> run) => tests.Add(("Two-tool runner: " + name, run));
        Test("complete material can submit in one model call without memory or review bureaucracy", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((_,_) => Submit(source));
            var runner = new CodingAgentRunner(model); var events = new List<CodingDiagnosticEvent>(); runner.Diagnostic += events.Add;
            await runner.RunAsync(session, Backend, new(MaximumCalls:1));
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 1 } && session.Memory.Attempts.Length == 1);
            Assert(session.CurrentDirective!.Prompt.EndsWith(CodingDirectivePolicy.ExecutionBoundary));
            Assert(events[0].Event == CodingDiagnosticKind.Started && events[^1].Event == CodingDiagnosticKind.Completed);
            Assert(events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1,events.Count).Select(i => (long)i)));
            Assert(!JsonSerializer.Serialize(events).Contains(source.Snapshot.Content.Text!));
        });
        Test("native assistant call and matching tool result are supplied on the next request", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n == 1 ? Retrieve(source, 8100, 40) : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session, Backend);
            var next = model.Requests[1].Messages;
            Assert(next.Length == 3 && next[1].Role == AgentRole.Assistant && next[2].Role == AgentRole.Tool);
            Assert(next[1].Call!.CallId == next[2].ToolCallId && next[1].Call!.Name == "retrieve_material");
            Assert(next[2].Content.Text!.Contains(source.Snapshot.Content.Text!.Substring(8100,40)));
            AgentModelClient.BuildMessages(model.Requests[1]);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback);
        });
        Test("unlimited runs exceed twelve calls when retrieval provides new passages", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n <= 16 ? Retrieve(source, 8100 + n * 100, 40) : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session, Backend);
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 17 });
            foreach (var request in model.Requests) AgentModelClient.BuildMessages(request);
        });
        Test("third repeated read is cached and still permits submission after intervening progress", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n switch { 1 or 3 or 5 => Retrieve(source,8100,20), 2 or 4 => Retrieve(source,8300+n*100,20), _ => Submit(source) });
            var runner = new CodingAgentRunner(model); var events = new List<CodingDiagnosticEvent>(); runner.Diagnostic += events.Add;
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 6 });
            Assert(events.Count(e => e.Step?.Cached == true && e.Event == CodingDiagnosticKind.ToolCompleted) == 2);
        });
        Test("continuous unchanged retrieval warns then pauses with a balanced resumable checkpoint", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n <= 7 ? Retrieve(source,8100,20) : Submit(source));
            var runner = new CodingAgentRunner(model); var events = new List<CodingDiagnosticEvent>(); runner.Diagnostic += events.Add;
            await runner.RunAsync(session, Backend);
            Assert(runner.Latest is { State: CodingRunState.Paused, ErrorCode: "coding_no_progress", Calls: 7 } && runner.CanRetry);
            Assert(events[^1].Event == CodingDiagnosticKind.Paused && events[^1].Step?.NoProgressSteps == 6);
            Assert(session.Memory.Attempts.IsEmpty && model.Requests[^1].Messages.Any(m => m.Content.Text?.Contains("No new evidence") == true));
            await runner.RetryAsync(session);
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 1 } && !runner.CanRetry);
            AgentModelClient.BuildMessages(model.Requests[^1]);
        });
        Test("varying queries with empty results cannot evade the no-progress detector", async () =>
        {
            var (session, _) = Session();
            var model = new Scripted((n,_) => Action("retrieve_material", new MaterialQuery("absent-query-" + n)));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session, Backend);
            Assert(runner.Latest is { State: CodingRunState.Paused, Calls: 6 });
        });
        Test("retry resumes the failed model step without rerunning completed retrieval or including new material", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n switch { 1 => Retrieve(source,8100,20), 2 => throw new SafeFailure("http_503"), _ => Submit(source) });
            var runner = new CodingAgentRunner(model); var events = new List<CodingDiagnosticEvent>(); runner.Diagnostic += events.Add;
            await runner.RunAsync(session, Backend);
            Assert(runner.CanRetry && session.Memory.Attempts.IsEmpty);
            session.AddObservation(new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText("new-pending-private-feedback"), 0, DateTimeOffset.UtcNow));
            await runner.RetryAsync(session);
            Assert(model.Requests[1].Messages.SequenceEqual(model.Requests[2].Messages));
            Assert(!model.Requests[2].Messages.Any(m => m.Content.Text?.Contains("new-pending-private-feedback") == true));
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 1 });
            Assert(events.Any(e => e.Event == CodingDiagnosticKind.Resumed) && events[^1].Step?.CheckpointResumed == true);
        });
        Test("successful turns continue the same native conversation with updated state", async () =>
        {
            var (session, source) = Session(); var model = new Scripted((_,_) => Submit(source)); var runner = new CodingAgentRunner(model);
            await runner.RunAsync(session, Backend);
            var first = session.Memory.Attempts[0];
            session.AddObservation(new(Guid.NewGuid(),InputKind.ClipboardText,Payload.FromText("new returned source"),0,DateTimeOffset.UtcNow));
            await runner.RunAsync(session, Backend);
            var next = model.Requests[1].Messages;
            Assert(next.Count(m => m.Role == AgentRole.User) == 2 && next.Count(m => m.Role == AgentRole.Assistant) == 1);
            Assert(next.Any(m => m.Role == AgentRole.Tool && m.Content.Text!.Contains(first.Id.ToString())));
            Assert(next[^1].Content.Text!.Contains("new returned source"));
            AgentModelClient.BuildMessages(model.Requests[1]);
        });
        Test("invalid evidence is returned as a correlated tool error and repaired atomically", async () =>
        {
            var (session, source) = Session();
            var invalid = Instruction(source) with { Evidence = [new(Guid.NewGuid(),0,1)] };
            var model = new Scripted((n,_) => n == 1 ? Action("submit_result", new CodingSubmission(invalid)) : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session, Backend);
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 2 });
            var next = model.Requests[1].Messages;
            Assert(next[^1].Role == AgentRole.Tool && next[^1].ToolCallId == next[^2].Call!.CallId && next[^1].Content.Text!.Contains("coding_evidence_invalid"));
            Assert(session.Memory.Attempts.Length == 1);
        });
        Test("unknown tools have no executor and receive a native error result", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n == 1 ? Action("execute_shell", new { cmd = "private-command" }) : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests[1].Messages[^1].Content.Text!.Contains("coding_action_unknown"));
        });
        Test("malformed argument strings retain their real call ID and receive a repair result", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n == 1 ? new(Guid.NewGuid().ToString("N"),"retrieve_material","{invalid") : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback);
            AgentModelClient.BuildMessages(model.Requests[1]);
        });
        Test("missing native calls receive bounded correction without inventing a call or dropping history", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n == 1 ? throw new SafeFailure("coding_tool_call_required") : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session,Backend);
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 2 });
            Assert(model.Requests[1].Messages.Length == 2 && model.Requests[1].Messages.All(m => m.Role == AgentRole.User));
            AgentModelClient.BuildMessages(model.Requests[1]);
            var invalid = new Scripted((_,_) => throw new SafeFailure("coding_tool_call_required"));
            var failed = new CodingAgentRunner(invalid); await failed.RunAsync(session,Backend);
            Assert(failed.Latest is { ErrorCode: "coding_action_repair_exhausted", Calls: 4 } && failed.CanRetry);
        });
        Test("duplicate provider call IDs cannot corrupt the checkpoint", async () =>
        {
            var (session, source) = Session(); var a = Retrieve(source,8100,20);
            var model = new Scripted((_,_) => a); var runner = new CodingAgentRunner(model); await runner.RunAsync(session, Backend);
            Assert(runner.Latest?.ErrorCode == "coding_history_invalid" && session.Memory.Attempts.IsEmpty);
            AgentModelClient.BuildMessages(model.Requests[^1]);
        });
        Test("stop rejects a late model result while a new run can finish", async () =>
        {
            var (session, source) = Session(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource<AgentAction>();
            var model = new AsyncModel(async (n,_,_) => { if(n==1) {entered.SetResult();return await release.Task;} return Submit(source); });
            var runner = new CodingAgentRunner(model); var pending = runner.RunAsync(session,Backend); await entered.Task; runner.Stop();
            await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert(runner.Latest?.State == CodingRunState.Cancelled && session.Memory.Attempts.IsEmpty);
            await runner.RunAsync(session,Backend); release.SetResult(Submit(source)); await Task.Yield();
            Assert(session.Memory.Attempts.Length == 1 && runner.Latest?.State == CodingRunState.AwaitingFeedback);
        });
        Test("clear removes checkpoints and forbids late publication", async () =>
        {
            var (session, source) = Session(); var release = new TaskCompletionSource<AgentAction>();
            var model = new AsyncModel(async (_,_,_) => await release.Task); var runner = new CodingAgentRunner(model);
            var task = runner.RunAsync(session,Backend); runner.Clear(); session.Clear(); await task;
            release.SetResult(Submit(source)); await Task.Yield();
            Assert(runner.Latest is null && !runner.CanRetry && session.Memory.Attempts.IsEmpty && session.Observations.Count==0);
        });
        Test("call and output budgets retain finished steps without publishing", async () =>
        {
            var (session, source) = Session();
            var model = new Scripted((n,_) => n <= 2 ? Retrieve(source,8100+n*100,20) : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session,Backend,new(MaximumCalls:2));
            Assert(runner.Latest?.ErrorCode == "coding_call_budget_exhausted" && runner.CanRetry && session.Memory.Attempts.IsEmpty);
            await runner.RetryAsync(session); Assert(runner.Latest is { State:CodingRunState.AwaitingFeedback,Calls:1 });
            var (other,otherSource) = Session();
            var tooLarge = new CodingAgentRunner(new Scripted((n,_) => n == 1 ? Action("submit_result",new CodingSubmission(Instruction(otherSource) with { Prompt = new string('x',2000) })) : Submit(otherSource)));
            await tooLarge.RunAsync(other,Backend,new(MaximumOutputChars:1024));
            Assert(tooLarge.Latest?.ErrorCode == "coding_output_budget_exhausted" && other.Memory.Attempts.IsEmpty);
            await tooLarge.RetryAsync(other); Assert(tooLarge.Latest?.State == CodingRunState.AwaitingFeedback);
        });
        Test("context pressure fails explicitly without deleting native call pairs", async () =>
        {
            var (session, source) = Session(); var model = new Scripted((_,_) => Retrieve(source,8100,6000));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session,Backend with { MaxInputBytes=40000 });
            Assert(runner.Latest?.ErrorCode == "coding_context_too_large" && model.Requests.Count==1 && runner.CanRetry);
            await runner.RetryAsync(session); Assert(model.Requests.Count==1 && session.Memory.Attempts.IsEmpty);
        });
        Test("changed memory invalidates an old retry checkpoint", async () =>
        {
            var (session, source) = Session(); var runner = new CodingAgentRunner(new Scripted((_,_) => throw new SafeFailure("http_503")));
            await runner.RunAsync(session,Backend); session.Commit(session.Freeze(),new([],[]),Instruction(source));
            try { await runner.RetryAsync(session); throw new Exception("Stale checkpoint accepted"); }
            catch(SafeFailure ex) { Assert(ex.Code=="coding_retry_stale"); }
        });
        Test("diagnostic observer failures do not abort a successful run", async () =>
        {
            var (session, source) = Session(); var runner = new CodingAgentRunner(new Scripted((_,_)=>Submit(source)));
            runner.Diagnostic += _ => throw new IOException("private-diagnostic-location");
            await runner.RunAsync(session,Backend); Assert(runner.Latest?.State==CodingRunState.AwaitingFeedback);
        });
        Test("batch retrieval returns exact original offsets and image packets keep paired history valid", async () =>
        {
            var (session, source) = Session();
            var image = session.AddObservation(new(Guid.NewGuid(), InputKind.ClipboardImage, new Payload("image evidence", [1,2,3], 1, 1), 0, DateTimeOffset.UtcNow));
            var model = new Scripted((n,_) => n == 1 ? Action("retrieve_material", new MaterialQuery(Ranges:[new(source.Id,8100,30),new(image.Id,0,0)])) : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session,Backend with { SupportsImages=true });
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback);
            var messages = model.Requests[1].Messages;
            Assert(messages.Count(m => m.Content.HasImage) == 1 && messages[^1].Role == AgentRole.User && messages[^1].Content.HasImage);
            Assert(messages.Any(m => m.Role == AgentRole.Tool && m.Content.Text!.Contains("8100")));
            AgentModelClient.BuildMessages(model.Requests[1]);
        });
        Test("retrieval search pagination is bounded and default batches fit their result budget", async () =>
        {
            var (session, source) = Session();
            for(var i=0;i<5;i++) session.AddObservation(new(Guid.NewGuid(),InputKind.ClipboardText,Payload.FromText("needle-"+i+new string('x',13000)),0,DateTimeOffset.UtcNow));
            var model = new Scripted((n,_) => n switch { 1=>Action("retrieve_material",new MaterialQuery()), 2=>Action("retrieve_material",new MaterialQuery("needle",Offset:1,Limit:2)), _=>Submit(source) });
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session,Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback);
            using var result=JsonDocument.Parse(model.Requests[2].Messages.Last(m=>m.Role==AgentRole.Tool).Content.Text!);
            Assert(result.RootElement.GetProperty("passages").GetArrayLength()==2 && result.RootElement.GetProperty("more").GetBoolean());
        });
        Test("a text-only model receives a repairable image-capability result without image transmission", async () =>
        {
            var (session, source) = Session();
            var image = session.AddObservation(new(Guid.NewGuid(),InputKind.ClipboardImage,new Payload("old image",[1,2,3],1,1),0,DateTimeOffset.UtcNow));
            var model = new Scripted((n,_) => n == 1 ? Action("retrieve_material",new MaterialQuery(Ranges:[new(image.Id,0,0)])) : Submit(source));
            var runner = new CodingAgentRunner(model); await runner.RunAsync(session,Backend);
            Assert(runner.Latest?.State == CodingRunState.AwaitingFeedback && model.Requests.All(r=>r.Messages.All(m=>!m.Content.HasImage)));
            Assert(model.Requests[1].Messages[^1].Content.Text!.Contains("backend_has_no_vision"));
            AgentModelClient.BuildMessages(model.Requests[1]);
        });
    }
    internal static (CodingSession Session, Observation Source) Session()
    {
        var session = new CodingSession();
        var source = session.AddObservation(new(Guid.NewGuid(),InputKind.ClipboardText,Payload.FromText(new string('a',18000)),0,DateTimeOffset.UtcNow));
        return(session,source);
    }
    internal static EvidenceRef Ref(Observation source) => new(source.Id,0,Math.Min(100,source.Snapshot.Content.Text!.Length));
    internal static Directive Instruction(Observation source) => new("取得当前函数","直接转发即可","Return current Solve and its test feedback; do not edit yet.","完整函数与测试反馈","missing-source","gather","Obtain current source",[Ref(source)]);
    internal static AgentAction Submit(Observation source) => Action("submit_result",new CodingSubmission(Instruction(source)));
    internal static AgentAction Retrieve(Observation source,int start,int length) => Action("retrieve_material",new MaterialQuery(Ranges:[new(source.Id,start,length)]));
    internal static AgentAction Action<T>(string name,T value) => new(Guid.NewGuid().ToString("N"),name,CodingJson.Serialize(value));
    internal static void Assert(bool condition) { if(!condition) throw new Exception("Two-tool runner assertion failed"); }
    internal sealed class Scripted(Func<int,AgentModelRequest,AgentAction> next) : IAgentModelClient
    {
        public List<AgentModelRequest> Requests {get;}=[];
        public Task<AgentAction> NextAsync(AgentModelRequest request,CancellationToken token)
        { Requests.Add(request); return Task.FromResult(next(Requests.Count,request)); }
    }
    internal sealed class AsyncModel(Func<int,AgentModelRequest,CancellationToken,Task<AgentAction>> next) : IAgentModelClient
    {
        private int count;
        public Task<AgentAction> NextAsync(AgentModelRequest request,CancellationToken token)=>next(++count,request,token);
    }
}
