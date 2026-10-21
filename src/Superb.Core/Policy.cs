using System.Net;

namespace Superb.Core;

public static class Policy
{
    public const int AbsoluteInputLimit = 16 * 1024 * 1024;
    public const int MaximumPixels = 32_000_000;
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
            b.MaxOutputChars is < 1 or > 1_000_000 || b.MaxTokens is < 1 or > 32768 ||
            !double.IsFinite(b.Temperature) || b.Temperature is < 0 or > 2 || !Enum.IsDefined(b.TokenLimitParameter))
            throw new SafeFailure("backend_limits_invalid");
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
        if (p.Size > request.Backend.MaxInputBytes) throw new SafeFailure("input_too_large");
        if (p.HasImage && !request.Backend.SupportsImages) throw new SafeFailure("backend_has_no_vision");
        if (p.HasImage && (p.Width <= 0 || p.Height <= 0 || (long)p.Width * p.Height > MaximumPixels))
            throw new SafeFailure("image_dimensions_invalid");
    }
}

public interface IImageProcessor { Payload Apply(Payload input, ProcessStep step); }
public static class Preprocessor
{
    public static Payload Process(Payload input, Pipeline pipeline, IImageProcessor images, Action<string>? status = null)
    {
        var current = input;
        foreach (var step in pipeline.Steps)
        {
            try
            {
                var candidate = step.Kind switch
                {
                    StepKind.NormalizeText => current with { Text = current.Text?.Replace("\r\n", "\n").Replace('\r', '\n').Trim() },
                    StepKind.Template when step.Template.Contains("{input}", StringComparison.Ordinal) =>
                        current with { Text = step.Template.Replace("{input}", current.Text ?? "", StringComparison.Ordinal) },
                    StepKind.Template => throw new SafeFailure("template_missing_placeholder"),
                    StepKind.Crop or StepKind.Mask or StepKind.Resize => images.Apply(current, step),
                    _ => throw new SafeFailure("processing_step_unknown")
                };
                if (candidate.Size > Policy.AbsoluteInputLimit) throw new SafeFailure("processed_input_too_large");
                current = candidate;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (step.Required || !step.SkipOnFailure) throw new SafeFailure("processing_required_failed");
                status?.Invoke("optional_step_skipped:" + step.Kind);
            }
        }
        return current;
    }
}
