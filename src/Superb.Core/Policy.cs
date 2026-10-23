using System.Net;

namespace Superb.Core;

public static class Policy
{
    public const int AbsoluteInputLimit = 16 * 1024 * 1024;
    public const int MaximumPixels = 32_000_000;
    public const int MaximumImages = 5;
    public const int DefaultOutputTokens = 200_000;
    public const int MaximumOutputTokens = 200_000;
    public const int MaximumDeepSeekOutputTokens = 393216;
    public static int OutputTokenLimit(string endpoint) => DeepSeekSetup.IsOfficialEndpoint(endpoint) ? MaximumDeepSeekOutputTokens : MaximumOutputTokens;
    public static Uri ValidateBackend(Backend b)
    {
        if (b.Adapter != "chat-completions") throw new SafeFailure("adapter_unsupported");
        ValidateProxy(b);
        if (!Uri.TryCreate(b.Endpoint, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new SafeFailure("endpoint_invalid");
        var loopback = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && loopback && b.AllowLoopbackHttp))
            throw new SafeFailure("encrypted_transport_required");
        if (string.IsNullOrWhiteSpace(b.Model) || string.IsNullOrWhiteSpace(b.Id) ||
            b.TimeoutSeconds is < 1 or > 600 || b.MaxInputBytes is < 1 or > AbsoluteInputLimit ||
            b.MaxOutputChars is < 1 or > 1_000_000 || b.MaxTokens < 1 || b.MaxTokens > OutputTokenLimit(b.Endpoint) ||
            !double.IsFinite(b.Temperature) || b.Temperature is < 0 or > 2 || !Enum.IsDefined(b.TokenLimitParameter) || !Enum.IsDefined(b.ThinkingMode))
            throw new SafeFailure("backend_limits_invalid");
        if (b.ThinkingMode != ThinkingMode.Default && !DeepSeekSetup.IsOfficialEndpoint(b.Endpoint))
            throw new SafeFailure("thinking_mode_unsupported");
        return uri;
    }

    public static Uri? ValidateProxy(Backend b)
    {
        if (!Enum.IsDefined(b.ProxyMode)) throw new SafeFailure("proxy_mode_invalid");
        if (b.ProxyMode != ProxyMode.Custom) return null;
        if (!Uri.TryCreate(b.ProxyAddress, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is not ("" or "/") || uri.Port is < 1 or > 65535)
            throw new SafeFailure("proxy_address_invalid");
        return uri;
    }

    public static void Check(PreparedRequest request)
    {
        ValidateBackend(request.Backend);
        var p = request.Content;
        if (string.IsNullOrWhiteSpace(p.Text) && !p.HasImage) throw new SafeFailure("input_empty");
        if (request.SystemPrompt is null || request.SystemPrompt.Length > 16000) throw new SafeFailure("system_prompt_invalid");
        if (p.Size + System.Text.Encoding.UTF8.GetByteCount(request.SystemPrompt) > request.Backend.MaxInputBytes) throw new SafeFailure("input_too_large");
        if (p.HasImage && !request.Backend.SupportsImages) throw new SafeFailure("backend_has_no_vision");
        var images = p.Images.ToArray();
        if (images.Length > MaximumImages) throw new SafeFailure("image_queue_full");
        if (images.Any(i => i.Png.IsDefaultOrEmpty || i.Width <= 0 || i.Height <= 0 || (long)i.Width * i.Height > MaximumPixels) ||
            images.Sum(i => (long)i.Width * i.Height) > MaximumPixels) throw new SafeFailure("image_dimensions_invalid");
    }
}
