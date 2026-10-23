using System.Collections.Immutable;

namespace Superb.Core;

public enum InputKind { ClipboardText, ClipboardImage, Screen, ManualText }
public enum RequestState { Idle, PolicyCheck, Connecting, Waiting, Streaming, Completed, Failed, Cancelled, Thinking }
public enum RunState { Ready, Paused, Locked, ProtectionUnavailable }
public enum ProxyMode { Direct, System, Custom }
public enum TokenLimitParameter { MaxTokens, MaxCompletionTokens }
public enum ThinkingMode { Default, Disabled, Enabled }
public sealed class SafeFailure(string code) : Exception(code) { public string Code { get; } = code; }
public sealed record PixelRect(int X, int Y, int Width, int Height);
public sealed record PayloadImage(ImmutableArray<byte> Png, int Width, int Height);
public sealed record Payload(string? Text, ImmutableArray<byte> Png, int Width = 0, int Height = 0,
    ImmutableArray<PayloadImage> AdditionalImages = default)
{
    public bool HasImage => !Png.IsDefaultOrEmpty || !AdditionalImages.IsDefaultOrEmpty;
    public IEnumerable<PayloadImage> Images
    {
        get
        {
            if (!Png.IsDefaultOrEmpty) yield return new(Png, Width, Height);
            if (!AdditionalImages.IsDefaultOrEmpty) foreach (var item in AdditionalImages) yield return item;
        }
    }
    public long Size => (Text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(Text)) + Images.Sum(i => i.Png.IsDefaultOrEmpty ? 0L : i.Png.Length);
    public static Payload FromText(string text) => new(text, []);
}
public sealed record InputSnapshot(Guid Id, InputKind Kind, Payload Content, uint ClipboardSequence, DateTimeOffset CapturedAt);
public sealed record Backend(string Id, string Name, string Endpoint, string Model, string CredentialRef,
    bool SupportsImages = false, bool AllowLoopbackHttp = false, bool Stream = true,
    int TimeoutSeconds = 120, int MaxInputBytes = 8_388_608, int MaxOutputChars = 1_000_000,
    double Temperature = 0.2, int MaxTokens = Policy.DefaultOutputTokens, string Adapter = "chat-completions",
    ProxyMode ProxyMode = ProxyMode.Direct, string ProxyAddress = "",
    bool SendTemperature = true, TokenLimitParameter TokenLimitParameter = TokenLimitParameter.MaxTokens,
    ThinkingMode ThinkingMode = ThinkingMode.Default);
public sealed record PreparedRequest(InputSnapshot Snapshot, Payload Content, Backend Backend,
    string SystemPrompt = "", string? SceneId = null);
public sealed record Attempt(Guid Id, long Generation, PreparedRequest Request);
public sealed record RequestUpdate(Attempt Attempt, RequestState State, string Text = "", string? Code = null);
public sealed record BackendEvent(string? Delta = null, bool Completed = false, bool Waiting = false, bool Thinking = false);
