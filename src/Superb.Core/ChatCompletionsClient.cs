using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Superb.Core;

public interface ICredentialStore { string Read(string reference); }

public sealed class ChatCompletionsClient(ICredentialStore credentials) : IBackendClient, IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        MaxResponseHeadersLength = 32,
        ConnectTimeout = TimeSpan.FromSeconds(20)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // This is the only production network entry point. Recheck frozen policy here.
        Policy.Check(request);
        var backend = request.Backend;
        using var message = new HttpRequestMessage(HttpMethod.Post, Policy.ValidateBackend(backend));
        if (!string.IsNullOrEmpty(backend.CredentialRef))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Read(backend.CredentialRef));
        object content = request.Content.Text ?? "";
        if (request.Content.HasImage)
        {
            var parts = new List<object>();
            if (!string.IsNullOrEmpty(request.Content.Text)) parts.Add(new { type = "text", text = request.Content.Text });
            parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(request.Content.Png.AsSpan()) } });
            content = parts;
        }
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = backend.Model,
            messages = new[] { new { role = "user", content } },
            stream = backend.Stream, temperature = backend.Temperature, max_tokens = backend.MaxTokens
        }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400) throw new SafeFailure("redirect_blocked");
        if (!response.IsSuccessStatusCode) throw new SafeFailure("http_" + (int)response.StatusCode);
        yield return new(Waiting: true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (backend.Stream)
        {
            await foreach (var item in ReadSseAsync(stream, backend.MaxOutputChars, cancellationToken)) yield return item;
        }
        else
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > backend.MaxOutputChars * 8L + 65536) throw new SafeFailure("response_too_large");
                buffer.Write(chunk, 0, read);
            }
            using var doc = JsonDocument.Parse(buffer.ToArray());
            var choice = doc.RootElement.GetProperty("choices")[0];
            CheckFinish(choice);
            var text = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            yield return new(Delta: text);
            yield return new(Completed: true);
        }
    }

    public static async IAsyncEnumerable<BackendEvent> ReadSseAsync(Stream stream, int maxOutputChars,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var line = new StringBuilder();
        var data = new StringBuilder();
        var chars = new char[2048];
        var total = 0L;
        var finished = false;
        int count;
        while ((count = await reader.ReadAsync(chars.AsMemory(), cancellationToken)) != 0)
        {
            total += count;
            if (total > maxOutputChars * 32L + 1_048_576) throw new SafeFailure("stream_too_large");
            for (var i = 0; i < count; i++)
            {
                var c = chars[i];
                if (c != '\n')
                {
                    line.Append(c);
                    if (line.Length > Math.Max(65536, maxOutputChars * 8L)) throw new SafeFailure("stream_event_too_large");
                    continue;
                }
                var value = line.ToString().TrimEnd('\r');
                line.Clear();
                if (value.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value.AsSpan(5).TrimStart(' '));
                    if (data.Length > Math.Max(65536, maxOutputChars * 8L)) throw new SafeFailure("stream_event_too_large");
                }
                else if (value.Length == 0 && data.Length > 0)
                {
                    var json = data.ToString();
                    data.Clear();
                    if (json == "[DONE]")
                    {
                        if (!finished) throw new SafeFailure("stream_missing_finish");
                        yield return new(Completed: true);
                        yield break;
                    }
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("error", out _)) throw new SafeFailure("stream_remote_error");
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    if (finished) throw new SafeFailure("stream_after_finish");
                    var choice = choices[0];
                    var delta = choice.GetProperty("delta");
                    if (delta.TryGetProperty("tool_calls", out _) || delta.TryGetProperty("function_call", out _))
                        throw new SafeFailure("tool_calls_not_supported");
                    if (delta.TryGetProperty("content", out var token) && token.ValueKind == JsonValueKind.String)
                        yield return new(Delta: token.GetString());
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                    {
                        CheckFinish(choice);
                        finished = true;
                    }
                }
            }
        }
        throw new SafeFailure("stream_incomplete");
    }
    private static void CheckFinish(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var reason) || reason.GetString() != "stop")
            throw new SafeFailure("response_not_complete");
    }
    public void Dispose() => http.Dispose();
}
