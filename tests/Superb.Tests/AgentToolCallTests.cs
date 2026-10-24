using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Superb.Core;
using Superb.Core.Coding;

internal static class AgentToolCallTests
{
    public static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Action body) => tests.Add(("Coding tools: " + name, () => { body(); return Task.CompletedTask; }));
        void Async(string name, Func<Task> body) => tests.Add(("Coding tools: " + name, body));
        Test("only two native tools are exposed and plain text envelopes are rejected", () =>
        {
            var tools = CodingTools.Definitions();
            Assert(tools.GetArrayLength() == 2);
            Assert(tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).SequenceEqual(new[] { "retrieve_material", "submit_result" }));
            var action = Parse(Response("retrieve_material", "{}"));
            Assert(action.CallId == "call-1" && action.Arguments == "{}");
            Fails("coding_tool_call_required", () => Parse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}"));
        });
        Test("native IDs are mandatory and ambiguous multiple calls are rejected", () =>
        {
            var valid = Response("retrieve_material", "{}");
            Fails("coding_action_invalid", () => Parse(valid.Replace("\"id\":\"call-1\",", "")));
            var call = new { id = "same", type = "function", function = new { name = "retrieve_material", arguments = "{}" } };
            Fails("coding_action_invalid", () => Parse(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call, call } } } } })));
            Fails("response_token_limit", () => Parse(Response("retrieve_material", "{}", "length")));
            Fails("response_content_filtered", () => Parse(Response("retrieve_material", "{}", "content_filter")));
        });
        Test("commentary and provider continuation remain attached to the native assistant item", () =>
        {
            var call = new { id = "call-1", type = "function", function = new { name = "retrieve_material", arguments = "{}" } };
            var action = Parse(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { content = "Reading current source", reasoning_content = "private-reasoning", tool_calls = new[] { call } } } } }));
            Assert(action.Commentary == "Reading current source" && action.ReasoningContent == "private-reasoning");
            Assert(!CodingJson.Serialize(action).Contains("private-reasoning"));
        });
        Test("complete call output pairs preserve IDs arguments and order", () =>
        {
            var call = new AgentAction("a", "retrieve_material", "{\"query\":\"Solve\"}", "Reading source", "private-continuation");
            var messages = Input("task").Add(new(AgentRole.Assistant, Payload.FromText("Reading source"), call))
                .Add(new(AgentRole.Tool, Payload.FromText("source"), ToolCallId: "a"));
            var request = new AgentModelRequest(Backend("https://example.test/v1/chat/completions"), "system", messages);
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(AgentModelClient.BuildMessages(request)));
            Assert(wire.RootElement[2].GetProperty("tool_calls")[0].GetProperty("id").GetString() == "a");
            Assert(wire.RootElement[3].GetProperty("role").GetString() == "tool" && wire.RootElement[3].GetProperty("tool_call_id").GetString() == "a");
            Assert(wire.RootElement[2].GetProperty("reasoning_content").GetString() == "private-continuation");
            Fails("coding_history_invalid", () => AgentModelClient.BuildMessages(request with { Messages = messages.RemoveAt(2) }));
            Fails("coding_history_invalid", () => AgentModelClient.BuildMessages(request with { Messages = messages.SetItem(2, messages[2] with { ToolCallId = "wrong" }) }));
        });
        Test("argument and wire budgets bound the full response", () =>
        {
            Fails("output_too_large", () => Parse(Response("retrieve_material", "{\"query\":\"long input\"}"), 5));
            Fails("response_too_large", () => Parse(new string(' ', 66000), 1));
        });
        Async("serialized input budget includes all function schemas", async () =>
        {
            var backend = Backend("https://example.invalid/v1/chat/completions") with { MaxInputBytes = 500 };
            await FailsAsync("input_too_large", () => new AgentModelClient(new Credentials()).NextAsync(new(backend, "system", Input("x")), default));
        });
        Async("HTTP transport always sends native tools without text-format negotiation", async () =>
        {
            using var server = new Server();
            var observed = server.ReplyAsync(Response("retrieve_material", "{}"));
            var backend = Backend(server.Endpoint) with { Stream = true, SendTemperature = false, TokenLimitParameter = TokenLimitParameter.MaxCompletionTokens };
            var action = await new AgentModelClient(new Credentials()).NextAsync(new(backend, "system", Input("evidence")), default);
            var (body, authorization) = await observed;
            Assert(action.Name == "retrieve_material" && authorization == "Bearer test-secret");
            Assert(!body.Contains("test-secret", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(body); var root = json.RootElement;
            Assert(!root.GetProperty("stream").GetBoolean());
            Assert(root.GetProperty("max_completion_tokens").GetInt32() == backend.MaxTokens);
            Assert(!root.TryGetProperty("temperature", out _) && !root.TryGetProperty("response_format", out _));
            Assert(root.GetProperty("tool_choice").GetString() == "auto" && root.GetProperty("tools").GetArrayLength() == 2);
            Assert(!root.GetProperty("parallel_tool_calls").GetBoolean());
        });
        Async("HTTP errors never expose response bodies or credentials", async () =>
        {
            using var server = new Server(); var reply = server.ReplyAsync("test-secret provider details", 401);
            await FailsAsync("http_401", () => new AgentModelClient(new Credentials()).NextAsync(new(Backend(server.Endpoint), "system", Input("evidence")), default));
            await reply;
        });
        Async("real HTTP tool conversation resumes the failed request with the same paired history", async () =>
        {
            using var server = new Server();
            var (session, source) = CodingRunnerTests.Session();
            var query = CodingJson.Serialize(new MaterialQuery(Ranges: [new(source.Id,8100,40)]));
            var first = server.ReplyAsync(Response("retrieve_material", query));
            var runner = new CodingAgentRunner(new AgentModelClient(new Credentials()));
            var diagnostics = new List<CodingDiagnosticEvent>(); runner.Diagnostic += diagnostics.Add;
            var run = runner.RunAsync(session, Backend(server.Endpoint));
            var (initial,_) = await first;
            var failed = server.ReplyAsync("private-server-error",503);
            await run;
            var (beforeRetry,_) = await failed;
            Assert(runner.Latest?.ErrorCode == "http_503" && runner.CanRetry && session.Memory.Attempts.IsEmpty);
            var response = Response("submit_result", CodingJson.Serialize(new CodingSubmission(CodingRunnerTests.Instruction(source))))
                .Replace("\"id\":\"call-1\"", "\"id\":\"call-2\"");
            var resumed = server.ReplyAsync(response);
            await runner.RetryAsync(session);
            var (afterRetry,_) = await resumed;
            using var before = JsonDocument.Parse(beforeRetry); using var after = JsonDocument.Parse(afterRetry);
            Assert(before.RootElement.GetProperty("messages").GetRawText() == after.RootElement.GetProperty("messages").GetRawText());
            var history = after.RootElement.GetProperty("messages");
            Assert(history.GetArrayLength() == 4 && history[2].GetProperty("role").GetString() == "assistant");
            Assert(history[2].GetProperty("tool_calls")[0].GetProperty("id").GetString() == history[3].GetProperty("tool_call_id").GetString());
            Assert(history[3].GetProperty("content").GetString()!.Contains("passages"));
            Assert(runner.Latest is { State: CodingRunState.AwaitingFeedback, Calls: 1 } && session.Memory.Attempts.Length == 1);
            Assert(!JsonSerializer.Serialize(diagnostics).Contains("private material") && !JsonSerializer.Serialize(diagnostics).Contains("private-server-error"));
        });
        Async("redirects are not followed", async () =>
        {
            using var server = new Server(); var reply = server.ReplyAsync("", 302);
            await FailsAsync("redirect_blocked", () => new AgentModelClient(new Credentials()).NextAsync(new(Backend(server.Endpoint), "system", Input("evidence")), default));
            await reply;
        });
        Async("cancellation interrupts an in-flight response", async () =>
        {
            using var server = new Server(); using var cancellation = new CancellationTokenSource();
            var pending = new AgentModelClient(new Credentials()).NextAsync(new(Backend(server.Endpoint), "system", Input("evidence")), cancellation.Token);
            var context = await server.Listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { await pending; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            context.Response.Close();
        });
        Async("transport metrics measure actual HTTP and redact provider metadata", async () =>
        {
            using var server = new Server();
            var trace = new CodingTransportTrace();
            var request = new AgentModelRequest(Backend(server.Endpoint), "system", Input("private-input"), trace);
            var pending = new AgentModelClient(new Credentials()).NextAsync(request, default);
            var context = await server.Listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await context.Request.InputStream.CopyToAsync(Stream.Null);
            context.Response.Headers["x-request-id"] = "secret-provider-request-id";
            context.Response.Headers["Server-Timing"] = "queue;dur=12.5;desc=private-metadata, inference;dur=45, private-metric;dur=999";
            var bytes = Encoding.UTF8.GetBytes(Response("retrieve_material", "{}"));
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            await pending;
            var metrics = trace.Snapshot;
            Assert(metrics.Stage == CodingTransportStage.Completed && metrics.ConnectionMilliseconds >= 0 && metrics.UploadMilliseconds >= 0);
            Assert(metrics.HeadersReceivedMilliseconds >= metrics.ConnectionMilliseconds && metrics.FirstByteMilliseconds >= metrics.HeadersReceivedMilliseconds);
            Assert(metrics.ResponseBytes == bytes.Length && metrics.ResponseContentLength == bytes.Length && metrics.RequestBytes > 0);
            Assert(metrics.ServerQueueMilliseconds == 12.5 && metrics.ServerProcessingMilliseconds == 45 && metrics.RequestIdFingerprint?.Length == 24);
            var json = JsonSerializer.Serialize(metrics);
            Assert(!json.Contains("secret-provider") && !json.Contains("private-") && !json.Contains("127.0.0.1"));
            var frozen = trace.Snapshot; await Task.Delay(20); Assert(trace.Snapshot == frozen);
        });
        Async("transport distinguishes header wait, first body byte wait and a stalled body", async () =>
        {
            foreach (var stage in new[] { CodingTransportStage.WaitingForResponseHeaders, CodingTransportStage.WaitingForResponseBody, CodingTransportStage.ReadingResponseBody })
            {
                using var server = new StalledServer(stage);
                var trace = new CodingTransportTrace();
                var pending = new AgentModelClient(new Credentials()).NextAsync(new(Backend(server.Endpoint) with { TimeoutSeconds = 1 }, "system", Input("evidence"), trace), default);
                await server.Ready;
                await FailsAsync("request_timeout", () => pending);
                var metrics = trace.Snapshot;
                Assert(metrics.Stage == stage && metrics.ConnectionMilliseconds >= 0 && metrics.UploadMilliseconds >= 0);
                Assert((metrics.HeadersReceivedMilliseconds is not null) == (stage != CodingTransportStage.WaitingForResponseHeaders));
                Assert((metrics.FirstByteMilliseconds is not null) == (stage == CodingTransportStage.ReadingResponseBody));
                Assert(metrics.ResponseBytes == (stage == CodingTransportStage.ReadingResponseBody ? 1 : 0));
                if (stage == CodingTransportStage.ReadingResponseBody) Assert(metrics.MillisecondsSinceLastByte >= 500);
            }
        });
        Async("runner retains the stalled HTTP stage on timeout and does not classify stop as timeout", async () =>
        {
            using var server = new StalledServer(CodingTransportStage.ReadingResponseBody);
            var session = new CodingSession();
            session.AddObservation(new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText("private-source"), 0, DateTimeOffset.UtcNow));
            var runner = new CodingAgentRunner(new AgentModelClient(new Credentials()));
            var events = new List<CodingDiagnosticEvent>(); runner.Diagnostic += events.Add;
            await runner.RunAsync(session, Backend(server.Endpoint) with { TimeoutSeconds = 1 });
            Assert(runner.Latest is { State: CodingRunState.Failed, ErrorCode: "request_timed_out", Transport.Stage: CodingTransportStage.ReadingResponseBody });
            Assert(events[^1].Transport?.ResponseBytes == 1 && events[^2].Event == CodingDiagnosticKind.CallFailed);
            using var stoppedServer = new StalledServer(CodingTransportStage.WaitingForResponseHeaders);
            var stopped = new CodingAgentRunner(new AgentModelClient(new Credentials()));
            var stoppedEvents = new List<CodingDiagnosticEvent>(); stopped.Diagnostic += stoppedEvents.Add;
            var task = stopped.RunAsync(session, Backend(stoppedServer.Endpoint));
            await stoppedServer.Ready; stopped.Stop(); await task;
            Assert(stopped.Latest is { State: CodingRunState.Cancelled, ErrorCode: "request_cancelled" });
            Assert(stoppedEvents.All(e => e.Event != CodingDiagnosticKind.CallFailed && e.ErrorCode != "request_timed_out"));
        });
        Async("overlapping HTTP calls keep request metrics isolated", async () =>
        {
            using var stalled = new StalledServer(CodingTransportStage.WaitingForResponseHeaders);
            using var success = new Server();
            var first = new CodingTransportTrace(); var second = new CodingTransportTrace();
            using var cancellation = new CancellationTokenSource();
            var a = new AgentModelClient(new Credentials()).NextAsync(new(Backend(stalled.Endpoint), "system", Input("a"), first), cancellation.Token);
            await stalled.Ready;
            var reply = success.ReplyAsync(Response("retrieve_material", "{}"));
            await new AgentModelClient(new Credentials()).NextAsync(new(Backend(success.Endpoint), "system", Input(new string('b', 500)), second), default);
            await reply;
            Assert(first.Snapshot.Stage == CodingTransportStage.WaitingForResponseHeaders && first.Snapshot.ResponseBytes == 0);
            Assert(second.Snapshot.Stage == CodingTransportStage.Completed && second.Snapshot.RequestBytes > first.Snapshot.RequestBytes);
            cancellation.Cancel(); try { await a; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        });
        Async("DNS TCP and rejected TLS handshakes have numeric timings without certificate or address logging", async () =>
        {
            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
            certificateRequest.CertificateExtensions.Add(names.Build());
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var serving = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
                using var tls = new SslStream(socket.GetStream());
                try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellation.Token); }
                catch (Exception ex) when (ex is AuthenticationException or IOException) { }
            });
            var trace = new CodingTransportTrace();
            var endpoint = $"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/chat/completions";
            await FailsAsync("network_tls_failed", () => new AgentModelClient(new Credentials()).NextAsync(new(Backend(endpoint), "system", Input("evidence"), trace), default));
            await serving;
            var metrics = trace.Snapshot;
            Assert(metrics.Stage == CodingTransportStage.TlsHandshake && metrics.DnsMilliseconds >= 0 && metrics.TcpMilliseconds >= 0 && metrics.TlsMilliseconds >= 0);
            Assert(!JsonSerializer.Serialize(metrics).Contains("localhost"));
        });
    }

    private sealed class StalledServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(8));
        private readonly Task serving;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready => ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public string Endpoint { get; }
        public StalledServer(CodingTransportStage stage)
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/chat/completions";
            serving = Serve(stage);
        }
        private async Task Serve(CodingTransportStage stage)
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                string? line; var size = 0;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellation.Token)))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) size = int.Parse(line[15..]);
                await reader.ReadBlockAsync(new char[size].AsMemory(), cancellation.Token);
                if (stage != CodingTransportStage.WaitingForResponseHeaders)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n"), cancellation.Token);
                    if (stage == CodingTransportStage.ReadingResponseBody) await stream.WriteAsync(new byte[] { 32 }, cancellation.Token);
                    await stream.FlushAsync(cancellation.Token);
                }
                ready.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ready.TrySetException(ex); }
        }
        public void Dispose() { cancellation.Cancel(); listener.Stop(); serving.GetAwaiter().GetResult(); cancellation.Dispose(); }
    }
    private static System.Collections.Immutable.ImmutableArray<AgentMessage> Input(string text) => [new(AgentRole.User, Payload.FromText(text))];
    private static Backend Backend(string endpoint) => new("coding", "test", endpoint, "test-model", "test-reference", AllowLoopbackHttp: true, TimeoutSeconds: 5);
    private static AgentAction Parse(string json, int max = 10000) => AgentModelClient.ParseResponse(Encoding.UTF8.GetBytes(json), max);
    private static string Response(string name, string arguments, string finish = "tool_calls") => JsonSerializer.Serialize(new
    { choices = new[] { new { finish_reason = finish, message = new { tool_calls = new[] { new { id = "call-1", type = "function", function = new { name, arguments } } }, reasoning_content = "private material" } } } });
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
