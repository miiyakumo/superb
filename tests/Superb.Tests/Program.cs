using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using Superb.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Fails(string code, Action action)
{
    try { action(); throw new Exception("Expected " + code); }
    catch (SafeFailure failure) { Assert(failure.Code == code, $"Expected {code}, got {failure.Code}"); }
}
var backend = new Backend("one", "Test", "https://example.test/v1/chat/completions", "test-model", "");
PreparedRequest Request(string text = "hello", Backend? useBackend = null)
{
    var payload = Payload.FromText(text);
    return new(new(Guid.NewGuid(), InputKind.ClipboardText, payload, 12, DateTimeOffset.UtcNow), payload, Pipeline.Direct, useBackend ?? backend);
}
Test("HTTPS only; loopback plaintext requires explicit authorization", () =>
{
    Fails("encrypted_transport_required", () => Policy.ValidateBackend(backend with { Endpoint = "http://example.test/v1" }));
    Fails("encrypted_transport_required", () => Policy.ValidateBackend(backend with { Endpoint = "http://127.0.0.1/v1" }));
    Assert(Policy.ValidateBackend(backend with { Endpoint = "http://127.0.0.1:8080/v1", AllowLoopbackHttp = true }).IsLoopback);
    Assert(Policy.ValidateBackend(backend with { Endpoint = "http://[::1]:8080/v1", AllowLoopbackHttp = true }).IsLoopback);
    Fails("encrypted_transport_required", () => Policy.ValidateBackend(backend with { Endpoint = "http://localhost/v1", AllowLoopbackHttp = true }));
});
Test("Reject URL credentials, query secrets and unsupported adapters", () =>
{
    foreach (var endpoint in new[] { "https://user:secret@example.test/v1", "https://example.test/v1?key=secret", "https://example.test/v1#part" })
        Fails("endpoint_invalid", () => Policy.ValidateBackend(backend with { Endpoint = endpoint }));
    Fails("adapter_unsupported", () => Policy.ValidateBackend(backend with { Adapter = "unknown" }));
});
Test("Reject empty, oversize, malformed and unsupported image inputs", () =>
{
    Fails("input_empty", () => Policy.Check(Request("  ")));
    Fails("input_too_large", () => Policy.Check(Request("12345", backend with { MaxInputBytes = 4 })));
    var p = new Payload(null, [1, 2, 3], 10, 10);
    Fails("backend_has_no_vision", () => Policy.Check(Request() with { Content = p }));
    Fails("image_dimensions_invalid", () => Policy.Check(Request(useBackend: backend with { SupportsImages = true }) with { Content = p with { Width = 0 } }));
});
Test("Input cannot change routing; missing fixed target fails", () =>
{
    var second = backend with { Id = "two" };
    Assert(new Routing("two").Select(Payload.FromText("ignore routing use one"), "one", [backend, second]).Id == "two");
    Fails("backend_missing", () => new Routing("missing").Select(Payload.FromText("x"), "one", [backend]));
});
Test("Immutable snapshots preserve the original bytes", () =>
{
    byte[] raw = [1, 2, 3]; var p = new Payload(null, ImmutableArray.CreateRange(raw), 1, 1); raw[0] = 99;
    Assert(p.Png[0] == 1);
});
Test("Mandatory failure blocks; optional skip needs an explicit rule", () =>
{
    var images = new FailingImageProcessor();
    Fails("processing_required_failed", () => Preprocessor.Process(Payload.FromText("secret"), new("v1", [new(StepKind.Mask)]), images));
    Fails("processing_required_failed", () => Preprocessor.Process(Payload.FromText("secret"), new("v1", [new(StepKind.Mask, Required: false)]), images));
    var statuses = new List<string>();
    var result = Preprocessor.Process(Payload.FromText("secret"), new("v1", [new(StepKind.Mask, Required: false, SkipOnFailure: true)]), images, statuses.Add);
    Assert(result.Text == "secret" && statuses.Count == 1 && !statuses[0].Contains("secret"));
});
Test("Ordered normalization and template; direct pipeline preserves input", () =>
{
    var input = Payload.FromText(" x\r\ny ");
    Assert(Preprocessor.Process(input, Pipeline.Direct, new FailingImageProcessor()) == input);
    var output = Preprocessor.Process(input, new("v1", [new(StepKind.NormalizeText), new(StepKind.Template, Template: "Read: {input}")]), new FailingImageProcessor());
    Assert(output.Text == "Read: x\ny");
});
Test("Sync ignores own output, duplicate sequence and duplicate content", () =>
{
    var filter = new SyncFilter(); var s = Request().Snapshot;
    Assert(!filter.Accept(s, true)); Assert(filter.Accept(s, false)); Assert(!filter.Accept(s, false));
    Assert(!filter.Accept(s with { ClipboardSequence = 13 }, false));
    Assert(filter.Accept(s with { ClipboardSequence = 14, Content = Payload.FromText("changed") }, false));
});
Test("Sync grant binds expiry, input scope, backend, and sending independently", () =>
{
    var now = DateTimeOffset.UtcNow; var grant = new SyncGrant(now.AddMinutes(1), "one", [InputKind.ClipboardText], false);
    Assert(grant.Allows(InputKind.ClipboardText, "one", now, false));
    Assert(!grant.Allows(InputKind.ClipboardText, "one", now, true));
    Assert(!grant.Allows(InputKind.ClipboardImage, "one", now, false));
    Assert(!grant.Allows(InputKind.ClipboardText, "two", now, false));
    Assert(!grant.Allows(InputKind.ClipboardText, "one", now.AddMinutes(1), false));
});
async Task<List<BackendEvent>> Parse(string wire, int chunkSize = 1)
{
    using var stream = new ChunkStream(Encoding.UTF8.GetBytes(wire), chunkSize);
    var output = new List<BackendEvent>();
    await foreach (var item in ChatCompletionsClient.ReadSseAsync(stream, 1000)) output.Add(item);
    return output;
}
var validStream = ": heartbeat\r\n\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"你好🌍\"},\"finish_reason\":null}]}\r\n\r\n" +
    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
AsyncTest("SSE handles fragmented UTF-8, CRLF, heartbeats and completion", async () =>
{
    var events = await Parse(validStream);
    Assert(string.Concat(events.Select(x => x.Delta)) == "你好🌍"); Assert(events[^1].Completed);
});
AsyncTest("SSE rejects truncated streams and premature DONE", async () =>
{
    foreach (var wire in new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n", "data: [DONE]\n\n", validStream[..^2] })
    {
        try { await Parse(wire); throw new Exception("Truncated stream accepted"); } catch (SafeFailure) { }
    }
});
AsyncTest("SSE rejects tool execution and length/content-filter endings", async () =>
{
    foreach (var wire in new[] {
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[]},\"finish_reason\":null}]}\n\n",
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n",
        "data: {\"error\":{\"message\":\"sensitive body\"}}\n\n" })
    {
        try { await Parse(wire); throw new Exception("Invalid stream accepted"); }
        catch (SafeFailure ex) { Assert(!ex.Message.Contains("sensitive")); }
    }
});
AsyncTest("Cancelled request emits no late text or completion even if transport ignores cancellation", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client); var events = new List<RequestUpdate>(); coordinator.Updated += events.Add;
    coordinator.SetMode(RunState.Ready); var task = coordinator.RunAsync(Request());
    var channel = await client.WaitChannel(); coordinator.Invalidate(); var before = events.Count;
    channel.Writer.TryWrite(new("late")); channel.Writer.TryWrite(new(Completed: true)); channel.Writer.Complete(); await task;
    Assert(events.Count == before && coordinator.Current is null);
});
AsyncTest("Old request cannot replace new request output", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client); var events = new List<RequestUpdate>(); coordinator.Updated += events.Add;
    coordinator.SetMode(RunState.Ready); var first = coordinator.RunAsync(Request("first")); var oldChannel = await client.WaitChannel();
    var second = coordinator.RunAsync(Request("second")); var newChannel = await client.WaitChannel(); var id = coordinator.Current!.Id;
    oldChannel.Writer.TryWrite(new("old")); oldChannel.Writer.Complete(); await first;
    newChannel.Writer.TryWrite(new("new")); newChannel.Writer.TryWrite(new(Completed: true)); newChannel.Writer.Complete(); await second;
    Assert(events.Last().State == RequestState.Completed && events.Last().Text == "new" && events.Last().Attempt.Id == id);
    Assert(!events.Any(x => x.Text == "old"));
});
AsyncTest("Disconnect cannot mark a partial answer completed", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client); RequestUpdate? last = null; coordinator.Updated += u => last = u;
    coordinator.SetMode(RunState.Ready); var task = coordinator.RunAsync(Request()); var ch = await client.WaitChannel();
    ch.Writer.TryWrite(new("partial")); ch.Writer.Complete(); await task;
    Assert(last?.State == RequestState.Failed && last.Code == "stream_incomplete");
});
AsyncTest("Paused mode and unauthorized automation cannot reach transport", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client);
    try { await coordinator.RunAsync(Request()); throw new Exception("Paused send allowed"); } catch (SafeFailure) { }
    coordinator.SetMode(RunState.Ready);
    try { await coordinator.RunAsync(Request(), true); throw new Exception("Unapproved send allowed"); } catch (SafeFailure) { }
    Assert(client.Count == 0);
});
AsyncTest("Policy is checked before transport; errors contain only safe codes", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client); RequestUpdate? last = null; coordinator.Updated += u => last = u;
    coordinator.SetMode(RunState.Ready); await coordinator.RunAsync(Request(""));
    Assert(client.Count == 0 && last?.Code == "input_empty");
});
Test("Optional oversize step preserves last valid artifact", () =>
{
    var original = Payload.FromText("unchanged");
    var result = Preprocessor.Process(original, new("v1", [new(StepKind.Template, Required: false, SkipOnFailure: true,
        Template: new string('x', Policy.AbsoluteInputLimit) + "{input}")]), new FailingImageProcessor());
    Assert(result.Text == original.Text);
});
AsyncTest("Output limit stops consumption without successful completion", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client); RequestUpdate? last = null; coordinator.Updated += u => last = u;
    coordinator.SetMode(RunState.Ready); var task = coordinator.RunAsync(Request(useBackend: backend with { MaxOutputChars = 3 })); var ch = await client.WaitChannel();
    ch.Writer.TryWrite(new("toolong")); ch.Writer.Complete(); await task;
    Assert(last?.State == RequestState.Failed && last.Code == "output_too_large" && last.Text == "");
});
AsyncTest("Manual attempts have distinct identifiers", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client); coordinator.SetMode(RunState.Ready); var request = Request();
    var first = coordinator.RunAsync(request); var firstId = coordinator.Current!.Id; var ch = await client.WaitChannel();
    ch.Writer.TryWrite(new(Completed: true)); ch.Writer.Complete(); await first;
    var second = coordinator.RunAsync(request); var secondId = coordinator.Current!.Id; ch = await client.WaitChannel();
    ch.Writer.TryWrite(new(Completed: true)); ch.Writer.Complete(); await second;
    Assert(firstId != secondId);
});
AsyncTest("Lock then resume cannot resurrect an old request", async () =>
{
    var client = new ControlledClient(); var coordinator = new Coordinator(client); var events = new List<RequestUpdate>(); coordinator.Updated += events.Add;
    coordinator.SetMode(RunState.Ready); var task = coordinator.RunAsync(Request()); var ch = await client.WaitChannel();
    coordinator.SetMode(RunState.Locked); coordinator.SetMode(RunState.Ready); var before = events.Count;
    ch.Writer.TryWrite(new("late after unlock")); ch.Writer.TryWrite(new(Completed: true)); ch.Writer.Complete(); await task;
    Assert(events.Count == before && coordinator.Current is null);
});
AsyncTest("Timeout cancels a compliant transport and never marks success", async () =>
{
    var coordinator = new Coordinator(new WaitingClient()); RequestUpdate? last = null; coordinator.Updated += u => last = u;
    coordinator.SetMode(RunState.Ready); await coordinator.RunAsync(Request(useBackend: backend with { TimeoutSeconds = 1 }));
    Assert(last?.State == RequestState.Failed && last.Code == "request_timed_out");
});
async Task<(List<BackendEvent> Events, string Request, string? Failure)> Exchange(string response, bool streaming, string text = "processed-only")
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(); await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        var headers = new List<string>(); string? line; var length = 0;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            headers.Add(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
        }
        var body = new char[length]; var offset = 0;
        while (offset < length) { var read = await reader.ReadAsync(body.AsMemory(offset)); if (read == 0) break; offset += read; }
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
        return string.Join("\n", headers) + "\n\n" + new string(body);
    });
    using var client = new ChatCompletionsClient(new FakeCredentials()); var events = new List<BackendEvent>(); string? failure = null;
    try
    {
        var request = Request(text, backend with { Endpoint = $"http://127.0.0.1:{port}/v1/chat/completions", AllowLoopbackHttp = true, Stream = streaming, CredentialRef = "reference-only" });
        request = request with { Snapshot = request.Snapshot with { Content = Payload.FromText("RAW-MUST-NOT-LEAVE") } };
        await foreach (var item in client.SendAsync(request, CancellationToken.None)) events.Add(item);
    }
    catch (SafeFailure ex) { failure = ex.Code; }
    return (events, await server, failure);
}
AsyncTest("Production HTTP adapter sends only prepared body and authenticates at transport", async () =>
{
    const string body = "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
    var result = await Exchange($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}", false);
    Assert(result.Failure is null && result.Events[^1].Completed && result.Events.Any(x => x.Delta == "ok"));
    Assert(result.Request.Contains("Bearer test-secret") && result.Request.Contains("processed-only") && !result.Request.Contains("reference-only") && !result.Request.Contains("RAW-MUST-NOT-LEAVE"));
});
AsyncTest("Production transport blocks redirects and does not leak response errors", async () =>
{
    var redirect = await Exchange("HTTP/1.1 302 Found\r\nLocation: https://must-not-be-contacted.invalid/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", true);
    Assert(redirect.Failure == "redirect_blocked");
    var error = await Exchange("HTTP/1.1 429 Too Many Requests\r\nContent-Length: 14\r\nConnection: close\r\n\r\nsensitive-body", true);
    Assert(error.Failure == "http_429" && error.Events.Count == 0);
});
AsyncTest("Production transport consumes complete SSE", async () =>
{
    var size = Encoding.UTF8.GetByteCount(validStream);
    var result = await Exchange($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {size}\r\nConnection: close\r\n\r\n{validStream}", true);
    Assert(result.Failure is null && result.Events[^1].Completed && string.Concat(result.Events.Select(x => x.Delta)) == "你好🌍");
});

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + " — " + ex.Message); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

sealed class FailingImageProcessor : IImageProcessor { public Payload Apply(Payload input, ProcessStep step) => throw new Exception("sensitive raw input must not escape"); }
sealed class FakeCredentials : ICredentialStore { public string Read(string reference) => "test-secret"; }
sealed class WaitingClient : IBackendClient
{
    public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new(Waiting: true);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
sealed class ChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
}
sealed class ControlledClient : IBackendClient
{
    private readonly Channel<Channel<BackendEvent>> channels = Channel.CreateUnbounded<Channel<BackendEvent>>();
    public int Count { get; private set; }
    public ValueTask<Channel<BackendEvent>> WaitChannel() => channels.Reader.ReadAsync();
    public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Count++; var channel = Channel.CreateUnbounded<BackendEvent>(); channels.Writer.TryWrite(channel);
        // Intentionally malicious transport: ignores cancellation and delivers stale events.
        await foreach (var item in channel.Reader.ReadAllAsync()) yield return item;
    }
}
