using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Superb.Core;
using Superb.Core.Coding;

internal static class AgentProtocolTests
{
    public static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Action body) => tests.Add(("Coding protocol: " + name, () => { body(); return Task.CompletedTask; }));
        void Async(string name, Func<Task> body) => tests.Add(("Coding protocol: " + name, body));
        Test("both protocols return only the validated action, never reasoning", () =>
        {
            foreach (var protocol in Enum.GetValues<AgentProtocol>())
            {
                var action = Parse(Envelope("{\"name\":\"read_task_state\",\"arguments\":{}}", protocol), protocol);
                Assert(action.Name == "read_task_state" && action.Arguments.GetRawText() == "{}");
            }
            foreach (var name in new[] { "analysis_result", "review_result" })
                Assert(Parse(Envelope(JsonSerializer.Serialize(new { name, arguments = new { } }))).Name == name);
        });
        Test("official thinking tools use auto without disabling thinking or accepting plain text", () =>
        {
            foreach (var endpoint in new[] { DeepSeekSetup.Endpoint, "https://api.deepseek.com/v1/chat/completions" })
            foreach (var mode in Enum.GetValues<ThinkingMode>())
            {
                var choice = AgentModelClient.NativeToolChoice(Backend(endpoint) with { ThinkingMode = mode });
                if (mode == ThinkingMode.Disabled)
                    Assert(choice.GetProperty("function").GetProperty("name").GetString() == "coding_action");
                else Assert(choice.ValueKind == JsonValueKind.String && choice.GetString() == "auto");
            }
            foreach (var endpoint in new[] { "https://example.invalid/chat/completions", "https://api.deepseek.com.example.invalid/chat/completions" })
                Assert(AgentModelClient.NativeToolChoice(Backend(endpoint)).GetProperty("function").GetProperty("name").GetString() == "coding_action");
            Fails("response_not_complete", () => Parse(Envelope("{\"name\":\"read_task_state\",\"arguments\":{}}"), AgentProtocol.NativeTools));
        });
        Test("JSON output capability is enabled only for the known official endpoint and JSON protocol", () =>
        {
            foreach (var endpoint in new[] { DeepSeekSetup.Endpoint, "https://api.deepseek.com/v1/chat/completions" })
            foreach (var mode in Enum.GetValues<ThinkingMode>())
            {
                var backend = Backend(endpoint) with { ThinkingMode = mode };
                Assert(AgentModelClient.JsonResponseFormat(backend, AgentProtocol.JsonActions)!.Value.GetProperty("type").GetString() == "json_object");
                Assert(AgentModelClient.JsonResponseFormat(backend, AgentProtocol.NativeTools) is null);
            }
            foreach (var endpoint in new[] { "https://example.invalid/chat/completions", "https://api.deepseek.com.example.invalid/chat/completions" })
                Assert(AgentModelClient.JsonResponseFormat(Backend(endpoint), AgentProtocol.JsonActions) is null);
            Assert(AgentModelClient.JsonProtocolInstruction.Contains("{\"name\":\"read_task_state\",\"arguments\":{}}", StringComparison.Ordinal));
            Assert(AgentModelClient.JsonProtocolInstruction.Contains("analysis_result", StringComparison.Ordinal) &&
                AgentModelClient.JsonProtocolInstruction.Contains("review_result", StringComparison.Ordinal));
        });
        Test("protocol errors identify the malformed shape without exposing rejected content", () =>
        {
            var cases = new[]
            {
                ("", "action_content_empty"),
                ("secret-token is not json", "action_json_invalid"),
                ("{\"action\":\"read_task_state\",\"arguments\":{}}", "action_envelope_invalid"),
                ("{\"name\":\"secret-token\",\"arguments\":{}}", "action_name_unknown"),
                ("{\"name\":\"read_task_state\",\"arguments\":\"secret-token\"}", "action_arguments_not_object"),
                ("{\"name\":\"read_task_state\",\"arguments\":{\"secret-token\":1,\"secret-token\":2}}", "duplicate_json_property")
            };
            foreach (var (json, detail) in cases)
            {
                try { Parse(Envelope(json)); throw new Exception("Malformed output accepted"); }
                catch (SafeFailure failure)
                {
                    Assert(failure.Code == "coding_action_invalid" && failure.Message == "coding_action_invalid");
                    Assert(AgentModelClient.ProtocolFailureDetail(failure) == detail);
                    Assert(!failure.ToString().Contains("secret-token", StringComparison.Ordinal));
                }
            }
        });
        Test("strict JSON denies fences, unknown actions, duplicate fields, nonobject arguments and extra fields", () =>
        {
            foreach (var json in new[]
            {
                "```json\n{\"name\":\"read_task_state\",\"arguments\":{}}\n```",
                "{\"name\":\"execute_shell\",\"arguments\":{}}",
                "{\"name\":\"read_task_state\",\"arguments\":[],\"extra\":true}",
                "{\"name\":\"read_task_state\",\"arguments\":null}",
                "{\"name\":\"read_task_state\",\"arguments\":{},\"extra\":true}",
                "{\"name\":\"read_task_state\",\"name\":\"read_task_state\",\"arguments\":{}}",
                "{\"name\":\"read_task_state\",\"arguments\":{\"q\":1,\"q\":2}}",
                "{\"name\":\"read_task_state\"}", "[]", "{}"
            }) Fails("coding_action_invalid", () => Parse(Envelope(json)));
        });
        Test("missing or unsuccessful finish never executes an action", () =>
        {
            var action = "{\"name\":\"read_task_state\",\"arguments\":{}}";
            Fails("response_not_complete", () => Parse("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}"));
            Fails("response_token_limit", () => Parse(Envelope(action, finish: "length")));
            Fails("response_content_filtered", () => Parse(Envelope(action, finish: "content_filter")));
            Fails("response_interrupted", () => Parse(Envelope(action, finish: "aborted")));
            Fails("response_not_complete", () => Parse(Envelope(action, finish: "tool_calls")));
            Fails("response_not_complete", () => Parse(Envelope(action, AgentProtocol.NativeTools, "stop"), AgentProtocol.NativeTools));
        });
        Test("native tools reject multiple calls and wrong function names", () =>
        {
            var valid = Envelope("{\"name\":\"read_task_state\",\"arguments\":{}}", AgentProtocol.NativeTools);
            Fails("coding_action_invalid", () => Parse(valid.Replace("coding_action", "shell"), AgentProtocol.NativeTools));
            var call = new { type = "function", function = new { name = "coding_action", arguments = "{}" } };
            var multiple = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call, call } } } } });
            Fails("coding_action_invalid", () => Parse(multiple, AgentProtocol.NativeTools));
            var ambiguous = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { content = "do this too", tool_calls = new[] { call } } } } });
            Fails("coding_action_invalid", () => Parse(ambiguous, AgentProtocol.NativeTools));
        });
        Test("action and wire limits apply even when reasoning is discarded", () =>
        {
            Fails("output_too_large", () => Parse(Envelope("{\"name\":\"read_task_state\",\"arguments\":{}}"), max: 5));
            Fails("response_too_large", () => Parse(new string(' ', 66000), max: 1));
        });
        Async("serialized request includes protocol and native schema in input budget", async () =>
        {
            var client = new AgentModelClient(new Credentials());
            var backend = Backend("https://example.invalid/v1/chat/completions") with { MaxInputBytes = 500 };
            await FailsAsync("input_too_large", () => client.NextAsync(new(backend, AgentProtocol.NativeTools, "system", Payload.FromText("x")), default));
        });
        Async("loopback transport uses frozen settings and single required native function", async () =>
        {
            foreach (var protocol in Enum.GetValues<AgentProtocol>())
            {
                using var server = new Server();
                var observed = server.ReplyAsync(Envelope("{\"name\":\"read_task_state\",\"arguments\":{}}", protocol));
                var backend = Backend(server.Endpoint) with { Stream = true, SendTemperature = false, TokenLimitParameter = TokenLimitParameter.MaxCompletionTokens };
                var action = await new AgentModelClient(new Credentials()).NextAsync(new(backend, protocol, "system", Payload.FromText("evidence")), default);
                var (body, authorization) = await observed;
                Assert(action.Name == "read_task_state" && authorization == "Bearer test-secret");
                Assert(!body.Contains("test-secret", StringComparison.Ordinal));
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                Assert(!root.GetProperty("stream").GetBoolean());
                Assert(root.GetProperty("max_completion_tokens").GetInt32() == backend.MaxTokens);
                Assert(!root.TryGetProperty("temperature", out _));
                Assert(root.GetProperty("messages").GetArrayLength() == 2);
                if (protocol == AgentProtocol.NativeTools)
                {
                    Assert(root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString() == "coding_action");
                    Assert(root.GetProperty("tools").GetArrayLength() == 1 && !root.GetProperty("parallel_tool_calls").GetBoolean());
                }
                else Assert(!root.TryGetProperty("tools", out _));
                Assert(!root.TryGetProperty("response_format", out _));
            }
        });
        Async("HTTP failures never expose response body or credentials", async () =>
        {
            using var server = new Server();
            var reply = server.ReplyAsync("test-secret provider details", 401);
            await FailsAsync("http_401", () => new AgentModelClient(new Credentials()).NextAsync(new(Backend(server.Endpoint), AgentProtocol.JsonActions, "system", Payload.FromText("evidence")), default));
            await reply;
        });
        Async("redirects are not followed", async () =>
        {
            using var server = new Server();
            var reply = server.ReplyAsync("", 302);
            await FailsAsync("redirect_blocked", () => new AgentModelClient(new Credentials()).NextAsync(new(Backend(server.Endpoint), AgentProtocol.JsonActions, "system", Payload.FromText("evidence")), default));
            await reply;
        });
        Async("cancellation interrupts an in-flight response", async () =>
        {
            using var server = new Server();
            using var cancellation = new CancellationTokenSource();
            var pending = new AgentModelClient(new Credentials()).NextAsync(new(Backend(server.Endpoint), AgentProtocol.JsonActions, "system", Payload.FromText("evidence")), cancellation.Token);
            var context = await server.Listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await pending; throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            context.Response.Close();
        });
    }

    private static Backend Backend(string endpoint) => new("coding", "test", endpoint, "test-model", "test-reference", AllowLoopbackHttp: true, TimeoutSeconds: 5);
    private static AgentAction Parse(string json, AgentProtocol protocol = AgentProtocol.JsonActions, int max = 10000) => AgentModelClient.ParseResponse(Encoding.UTF8.GetBytes(json), protocol, max);
    private static string Envelope(string action, AgentProtocol protocol = AgentProtocol.JsonActions, string? finish = null)
    {
        object message = protocol == AgentProtocol.JsonActions
            ? new { content = action, reasoning_content = "private material" }
            : new { tool_calls = new[] { new { type = "function", function = new { name = "coding_action", arguments = action } } }, reasoning_content = "private material" };
        return JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish ?? (protocol == AgentProtocol.JsonActions ? "stop" : "tool_calls"), message } } });
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Fails(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (SafeFailure error) { Assert(error.Code == code && error.Message == code); }
    }
    private static async Task FailsAsync(string code, Func<Task> action)
    {
        try { await action(); throw new Exception("Expected " + code); }
        catch (SafeFailure error) { Assert(error.Code == code && error.Message == code); }
    }
    private sealed class Credentials : ICredentialStore { public string Read(string reference) => "test-secret"; }
    private sealed class Server : IDisposable
    {
        public HttpListener Listener { get; } = new();
        public string Endpoint { get; }
        public Server()
        {
            var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            Endpoint = $"http://127.0.0.1:{number}/v1/chat/completions";
            Listener.Prefixes.Add($"http://127.0.0.1:{number}/");
            Listener.Start();
        }
        public async Task<(string Body, string? Authorization)> ReplyAsync(string response, int status = 200)
        {
            var context = await Listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var authorization = context.Request.Headers["Authorization"];
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
            return (body, authorization);
        }
        public void Dispose() => Listener.Close();
    }
}
