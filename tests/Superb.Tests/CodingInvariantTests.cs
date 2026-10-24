using Superb.Core;
using Superb.Core.Coding;

internal static class CodingInvariantTests
{
    internal static void Add(List<(string Name,Func<Task> Run)> tests)
    {
        tests.Add(("Two-tool invariants: submission cannot promote an unverified outcome to completion",async()=>
        {
            var (session,source)=CodingRunnerTests.Session();
            var directive=CodingRunnerTests.Instruction(source);
            var model=new CodingRunnerTests.Scripted((_,_)=>CodingRunnerTests.Action("submit_result",new CodingSubmission(directive with {TaskComplete=true})));
            var runner=new CodingAgentRunner(model); await runner.RunAsync(session,new("test","test","https://example.test/v1/chat/completions","test",""));
            CodingRunnerTests.Assert(runner.Latest?.ErrorCode=="coding_action_repair_exhausted" && session.Memory.Attempts.IsEmpty);
        }));
        tests.Add(("Two-tool invariants: verified completion requires real feedback for the latest attempt",async()=>
        {
            var (session,source)=CodingRunnerTests.Session();
            var previous=session.Commit(session.Freeze(),new([],[]),CodingRunnerTests.Instruction(source));
            var feedback=session.AddObservation(new(Guid.NewGuid(),InputKind.ClipboardText,Payload.FromText("test suite: all requirements passed on the latest implementation"),0,DateTimeOffset.UtcNow));
            var evidence=CodingRunnerTests.Ref(feedback);
            var assessment=new AttemptAssessment(previous.Id,"missing-source","gather",AttemptOutcome.Verified,true,true,[evidence],[feedback.Id]);
            var result=new CodingSubmission(CodingRunnerTests.Instruction(feedback) with {TaskComplete=true},new([],[assessment]));
            var runner=new CodingAgentRunner(new CodingRunnerTests.Scripted((_,_)=>CodingRunnerTests.Action("submit_result",result)));
            await runner.RunAsync(session,new("test","test","https://example.test/v1/chat/completions","test",""));
            CodingRunnerTests.Assert(runner.Latest?.State==CodingRunState.AwaitingFeedback && session.Memory.Attempts.Length==2 && session.Memory.Attempts[0].Assessment?.Outcome==AttemptOutcome.Verified);
        }));
        tests.Add(("Two-tool invariants: duplicate argument fields never mutate task memory",async()=>
        {
            var (session,_)=CodingRunnerTests.Session();
            var runner=new CodingAgentRunner(new CodingRunnerTests.Scripted((_,_)=>new(Guid.NewGuid().ToString("N"),"retrieve_material","{\"query\":\"a\",\"query\":\"b\"}")));
            await runner.RunAsync(session,new("test","test","https://example.test/v1/chat/completions","test",""));
            CodingRunnerTests.Assert(runner.Latest?.ErrorCode=="coding_action_repair_exhausted" && session.Memory.Revision==0);
        }));
    }
}
