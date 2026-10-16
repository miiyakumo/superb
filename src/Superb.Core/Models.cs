using System.Collections.Immutable;

namespace Superb.Core;

public enum InputKind { ClipboardText, ClipboardImage, Screen }
public enum RequestState { Idle, Capturing, Processing, AwaitingApproval, PolicyCheck, Connecting, Waiting, Streaming, Completed, Failed, Cancelled }
public enum RunState { Ready, Paused, Locked, ProtectionUnavailable }
public enum StepKind { NormalizeText, Template, Crop, Mask, Resize }
public sealed class SafeFailure(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record PixelRect(int X, int Y, int Width, int Height);
public sealed record Payload(string? Text, ImmutableArray<byte> Png, int Width = 0, int Height = 0)
{
    public bool HasImage => !Png.IsDefaultOrEmpty;
    public long Size => (Text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(Text)) + (HasImage ? Png.Length : 0);
    public static Payload FromText(string text) => new(text, []);
}
public sealed record InputSnapshot(Guid Id, InputKind Kind, Payload Content, uint ClipboardSequence, DateTimeOffset CapturedAt);
public sealed record ProcessStep(StepKind Kind, bool Required = true, bool SkipOnFailure = false,
    string Template = "{input}", PixelRect? Rectangle = null, int MaxDimension = 1600);
public sealed record Pipeline(string Version, ImmutableArray<ProcessStep> Steps)
{
    public static Pipeline Direct { get; } = new("direct-v1", []);
}
public sealed record Backend(string Id, string Name, string Endpoint, string Model, string CredentialRef,
    bool SupportsImages = false, bool AllowLoopbackHttp = false, bool Stream = true,
    int TimeoutSeconds = 120, int MaxInputBytes = 8_388_608, int MaxOutputChars = 200_000,
    double Temperature = 0.2, int MaxTokens = 4096, string Adapter = "chat-completions");
public sealed record PreparedRequest(InputSnapshot Snapshot, Payload Content, Pipeline Pipeline, Backend Backend,
    ImmutableArray<string> ProcessingNotices = default);
public sealed record Attempt(Guid Id, long Generation, PreparedRequest Request);
public sealed record RequestUpdate(Attempt Attempt, RequestState State, string Text = "", string? Code = null);
public sealed record BackendEvent(string? Delta = null, bool Completed = false, bool Waiting = false);
public sealed record SyncGrant(DateTimeOffset ExpiresAt, string BackendId, ImmutableHashSet<InputKind> Inputs, bool AllowSend)
{
    public bool Allows(InputKind kind, string backend, DateTimeOffset now, bool sending) =>
        now < ExpiresAt && backend == BackendId && Inputs.Contains(kind) && (!sending || AllowSend);
}
public sealed record Routing(string? TextBackendId = null, string? ImageBackendId = null)
{
    public Backend Select(Payload content, string selectedId, IEnumerable<Backend> backends)
    {
        var id = (content.HasImage ? ImageBackendId : TextBackendId) ?? selectedId;
        return backends.FirstOrDefault(x => x.Id == id) ?? throw new SafeFailure("backend_missing");
    }
}
