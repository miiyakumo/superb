using System.Net.Http.Headers;
using System.Collections.Immutable;
using System.Text.Json;

namespace Superb.Core.Coding;

public sealed class AgentModelClient(ICredentialStore credentials) : IAgentModelClient
{
    public async Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SystemPrompt is null) throw new SafeFailure("system_prompt_invalid");
        var backend = request.Backend;
        var system = request.SystemPrompt;
        var messages = BuildMessages(request);
        var parts = request.Messages.SelectMany(m => m.Content.Images).ToArray();
        var text = string.Join("\n", request.Messages.Select(m => m.Content.Text));
        var aggregate = parts.Length == 0 ? Payload.FromText(text) : new Payload(text, parts[0].Png, parts[0].Width, parts[0].Height, parts.Skip(1).ToImmutableArray());
        Policy.Check(new(new(Guid.NewGuid(), InputKind.ManualText, aggregate, 0, DateTimeOffset.UtcNow), aggregate, backend, system));
        var body = new Dictionary<string, object>
        {
            ["model"] = backend.Model,
            ["messages"] = messages,
            ["stream"] = false,
            [backend.TokenLimitParameter == TokenLimitParameter.MaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = backend.MaxTokens,
            ["tools"] = CodingTools.Definitions(), ["tool_choice"] = "auto", ["parallel_tool_calls"] = false
        };
        if (backend.SendTemperature) body["temperature"] = backend.Temperature;
        if (backend.ThinkingMode != ThinkingMode.Default)
            body["thinking"] = new { type = backend.ThinkingMode == ThinkingMode.Disabled ? "disabled" : "enabled" };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(body);
        // The wire budget includes every history item, image and both tool schemas.
        if (serialized.LongLength > backend.MaxInputBytes) throw new SafeFailure("input_too_large");
        var trace = request.Transport ?? new CodingTransportTrace();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(backend.TimeoutSeconds));
        using var http = new HttpClient(ChatCompletionsClient.CreateHandler(backend)) { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(HttpMethod.Post, Policy.ValidateBackend(backend));
        if (!string.IsNullOrEmpty(backend.CredentialRef))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Read(backend.CredentialRef));
        message.Content = new ByteArrayContent(serialized);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var traceScope = trace.Begin(serialized.LongLength);
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            trace.ReceivedHeaders(response);
            if ((int)response.StatusCode is >= 300 and < 400) throw new SafeFailure("redirect_blocked");
            if (!response.IsSuccessStatusCode) throw new SafeFailure("http_" + (int)response.StatusCode);
            var maximumWireBytes = backend.MaxOutputChars * 8L + 65536;
            if (response.Content.Headers.ContentLength > maximumWireBytes) throw new SafeFailure("response_too_large");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                trace.ReceivedBytes(read);
                if (buffer.Length + read > maximumWireBytes) throw new SafeFailure("response_too_large");
                buffer.Write(bytes, 0, read);
            }
            trace.Parsing();
            var action = ParseResponse(buffer.ToArray(), backend.MaxOutputChars);
            trace.Complete();
            return action;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SafeFailure("request_timeout"); }
        catch (HttpRequestException ex) { throw new SafeFailure(ConnectionDiagnostics.CodeFor(ex)); }
    }

    // Validate complete call/output pairs before sending; never invent IDs or restart a transcript.
    public static IReadOnlyList<object> BuildMessages(AgentModelRequest request)
    {
        if (request.Messages.IsDefaultOrEmpty || request.Messages[0].Role != AgentRole.User) throw new SafeFailure("coding_history_invalid");
        var messages = new List<object> { new { role = "system", content = request.SystemPrompt } };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? pending = null;
        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case AgentRole.User when pending is null && message.Call is null && message.ToolCallId is null:
                    messages.Add(new { role = "user", content = UserContent(message.Content) }); break;
                case AgentRole.Assistant when pending is null && message.Call is { } call && message.ToolCallId is null:
                    if (!ValidId(call.CallId) || !ids.Add(call.CallId) || message.Content.HasImage) throw new SafeFailure("coding_history_invalid");
                    var assistant = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant", ["content"] = string.IsNullOrEmpty(call.Commentary) ? null : call.Commentary,
                        ["tool_calls"] = new[] { new { id = call.CallId, type = "function", function = new { name = call.Name, arguments = call.Arguments } } }
                    };
                    // Some Chat Completions providers require this continuation field. It stays in
                    // memory and on the configured transport; it is never returned to UI or logs.
                    if (call.ReasoningContent is not null) assistant["reasoning_content"] = call.ReasoningContent;
                    messages.Add(assistant); pending = call.CallId; break;
                case AgentRole.Tool when pending is not null && message.ToolCallId == pending && message.Call is null && !message.Content.HasImage:
                    messages.Add(new { role = "tool", tool_call_id = pending, content = message.Content.Text ?? "" }); pending = null; break;
                default: throw new SafeFailure("coding_history_invalid");
            }
        }
        if (pending is not null) throw new SafeFailure("coding_history_invalid");
        return messages;
    }
    private static object UserContent(Payload payload)
    {
        if (!payload.HasImage) return payload.Text ?? "";
        var parts = new List<object>();
        if (!string.IsNullOrEmpty(payload.Text)) parts.Add(new { type = "text", text = payload.Text });
        foreach (var image in payload.Images)
            parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image.Png.AsSpan()) } });
        return parts;
    }
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl);
    public static AgentAction ParseResponse(ReadOnlyMemory<byte> utf8, int maxOutputChars)
    {
        if (maxOutputChars < 1 || utf8.Length > maxOutputChars * 8L + 65536) throw new SafeFailure("response_too_large");
        try
        {
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement; CodingJson.CheckUniqueProperties(root);
            if (root.TryGetProperty("error", out _)) throw new SafeFailure("coding_remote_error");
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() != 1) throw new SafeFailure("coding_action_invalid");
            var choice = choices[0];
            var reason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String ? finish.GetString() : null;
            if (reason != "tool_calls") throw new SafeFailure(reason switch
            {
                "length" => "response_token_limit", "content_filter" => "response_content_filtered", "stop" => "coding_tool_call_required",
                "aborted" or "insufficient_system_resource" => "response_interrupted", _ => "response_not_complete"
            });
            var message = choice.GetProperty("message");
            var calls = message.GetProperty("tool_calls");
            if (calls.GetArrayLength() != 1 || calls[0].GetProperty("type").GetString() != "function") throw new SafeFailure("coding_action_invalid");
            var id = calls[0].GetProperty("id").GetString();
            var function = calls[0].GetProperty("function");
            var name = function.GetProperty("name").GetString();
            var arguments = function.GetProperty("arguments").GetString();
            if (!ValidId(id) || name is not { Length: > 0 and <= 128 } || arguments is null) throw new SafeFailure("coding_action_invalid");
            var commentary = message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null ? content.GetString() : null;
            var reasoning = message.TryGetProperty("reasoning_content", out var thought) && thought.ValueKind != JsonValueKind.Null ? thought.GetString() : null;
            if (arguments.Length + (commentary?.Length ?? 0) > maxOutputChars) throw new SafeFailure("output_too_large");
            return new(id!, name, arguments, commentary, reasoning);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        { throw new SafeFailure("coding_action_invalid"); }
    }
}
