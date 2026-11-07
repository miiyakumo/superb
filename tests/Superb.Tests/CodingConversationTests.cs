using System.Runtime.CompilerServices;
using Superb.Core;

internal static class CodingConversationTests
{
    private sealed class Model : IBackendClient
    {
        public List<PreparedRequest> Requests { get; } = [];
        public Func<PreparedRequest, string> Reply { get; set; } = r => r.SceneId == "coding" ? "answer" : "note";

        public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return new(Reply(request));
            yield return new(Completed: true);
        }
    }

    private static PreparedRequest Input(string text, int window = 4096)
    {
        var content = Payload.FromText(text);
        var backend = new Backend("test", "test", "https://example.test/v1", "model", "", ContextWindowTokens: window);
        return new(new(Guid.NewGuid(), InputKind.ClipboardText, content, 0, DateTimeOffset.UtcNow),
            content, backend, "BUSINESS", "coding", "SUMMARIZE");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static void Add(List<(string, Func<Task>)> tests)
    {
        tests.Add(("Coding: continuation, scene isolation, replacement and task clearing", async () =>
        {
            var model = new Model();
            var coordinator = new Coordinator(model);
            coordinator.SetMode(RunState.Ready);
            await coordinator.RunAsync(Input("requirements"));
            var first = coordinator.Current!.Request;
            model.Reply = _ => "revised answer";
            await coordinator.RunAsync(first);
            await coordinator.RunAsync(Input("general") with { SceneId = "general" });
            Check(model.Requests[^1].History.IsEmpty, "Coding history leaked into general");
            await coordinator.RunAsync(Input("first diff"));
            var history = model.Requests[^1].History;
            Check(history.Select(m => (m.Role, m.Content.Text)).SequenceEqual(
                [("user", "requirements"), ("assistant", "revised answer")]), "Retry duplicated or lost a turn");
            await coordinator.RunAsync(Input("still failing"));
            Check(model.Requests[^1].History.Length == 4, "Third round lost the previous exchange");
            coordinator.SetMode(RunState.Locked);
            coordinator.SetMode(RunState.Ready);
            await coordinator.RunAsync(Input("another task"));
            Check(model.Requests[^1].History.IsEmpty, "Cleared task remained in history");
        }));
        tests.Add(("Coding: compact only completed history; failed final request retries its frozen summary", async () =>
        {
            var model = new Model();
            var coordinator = new Coordinator(model);
            coordinator.SetMode(RunState.Ready);
            await coordinator.RunAsync(Input(new string('x', 900)));
            await coordinator.RunAsync(Input("prior diff"));
            model.Reply = r => r.SceneId == "coding" ? throw new SafeFailure("http_503") : "summary";
            var current = Input("CURRENT-DIFF", 1024);
            await coordinator.RunAsync(current);
            Check(coordinator.Latest?.Code == "http_503", "Expected failed final request");
            var summaries = model.Requests.Where(r => r.SystemPrompt == "SUMMARIZE").ToArray();
            Check(summaries.Length >= 1 && summaries.All(r => r.Snapshot.Content == r.Content &&
                !r.Content.Text!.Contains("CURRENT-DIFF") && r.History.All(m =>
                    !m.Content.Text!.Contains("CURRENT-DIFF") && !m.Content.Text.Contains("BUSINESS"))),
                "Business prompt or current material reached summarization");
            var frozen = coordinator.Current!.Request;
            Check(frozen.Content == current.Content && frozen.History[0].Content.Text!.Contains("summary"), "Invalid final input");
            var sent = model.Requests.Count;
            model.Reply = _ => "recovered";
            await coordinator.RunAsync(frozen);
            Check(coordinator.State == RequestState.Completed && model.Requests.Count == sent + 1 &&
                model.Requests[^1].History.SequenceEqual(frozen.History), "Retry repeated compression");
        }));
        tests.Add(("Coding: exact 80 percent trigger uses the entire UTF-8 conversation", async () =>
        {
            foreach (var count in new[] { 509, 510 })
            {
                var model = new Model();
                var coordinator = new Coordinator(model);
                coordinator.SetMode(RunState.Ready);
                await coordinator.RunAsync(Input(new string('x', count), 1024));
                // Second request = 66 envelope + 12 user + count + 17 assistant + 6 answer + 3 UTF-8.
                await coordinator.RunAsync(Input("测", 1024));
                Check(coordinator.State == RequestState.Completed, "Analysis failed");
                Check(model.Requests.Count(r => r.SystemPrompt == "SUMMARIZE") == (count == 510 ? 1 : 0),
                    "Wrong threshold");
            }
        }));
        tests.Add(("Coding: stopping an in-flight summary cannot commit or send a late final request", async () =>
        {
            var client = new ControlledClient();
            var coordinator = new Coordinator(client);
            coordinator.SetMode(RunState.Ready);
            var first = coordinator.RunAsync(Input(new string('x', 650), 1024));
            var channel = await client.WaitChannel();
            channel.Writer.TryWrite(new("old answer"));
            channel.Writer.TryWrite(new(Completed: true));
            await first;
            var second = coordinator.RunAsync(Input("late diff", 1024));
            var summarizer = await client.WaitChannel();
            Check(coordinator.State == RequestState.Summarizing, "Expected a summary in flight");
            coordinator.Stop();
            summarizer.Writer.TryWrite(new("late summary"));
            summarizer.Writer.TryComplete();
            await second;
            Check(coordinator.State == RequestState.Cancelled && client.Count == 2, "Old summary published an answer");
        }));
        tests.Add(("Coding: first packet at soft and hard limits; one retry on explicit provider overflow", async () =>
        {
            var model = new Model();
            var coordinator = new Coordinator(model);
            coordinator.SetMode(RunState.Ready);
            // W=1024, R=256, B=768. System BUSINESS and the user envelope cost 66.
            await coordinator.RunAsync(Input(new string('x', 702), 1024));
            Check(coordinator.State == RequestState.Completed && model.Requests.Count == 1, "At-budget first input failed");
            coordinator.ClearConversation();
            await coordinator.RunAsync(Input(new string('x', 703), 1024));
            Check(coordinator.Latest?.Code == "context_length_exceeded" && model.Requests.Count == 1, "Oversize first turn was sent");
            await coordinator.RunAsync(Input(new string('x', 500)));
            bool reject = true;
            model.Reply = r =>
            {
                if (r.SceneId != "coding") return "note";
                if (reject) { reject = false; throw new SafeFailure("context_length_exceeded"); }
                return "ok";
            };
            await coordinator.RunAsync(Input("new log"));
            Check(coordinator.State == RequestState.Completed && model.Requests.Count == 5 &&
                model.Requests[^1].History[0].Content.Text!.Contains("note"), "Provider overflow recovery failed");
        }));
    }
}
