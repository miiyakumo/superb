namespace Superb.Core;

public static class BackendSetup
{
    // Accept common Base URLs without changing an explicitly supplied custom POST path.
    // Authorization and transport checks still run on the resulting URL before any request.
    public static string NormalizeEndpoint(string input)
    {
        var value = input.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new SafeFailure("endpoint_invalid");
        var path = uri.AbsolutePath.TrimEnd('/');
        var suffix = path switch
        {
            "" => "/v1/chat/completions",
            "/v1" => "/chat/completions",
            _ => ""
        };
        return suffix.Length > 0 || path.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? value.TrimEnd('/') + suffix : value;
    }
}
