using System.Text.Json;

namespace Superb.Core;

public sealed record ModelOption(string Id, bool SupportsImages = false);
public interface IModelCatalog
{
    Task<IReadOnlyList<ModelOption>> ListModelsAsync(Backend backend, CancellationToken cancellationToken);
}
public sealed record QuickSetupResult(Backend Backend, IReadOnlyList<ModelOption> Models);

public static class DeepSeekSetup
{
    public const string Endpoint = "https://api.deepseek.com/chat/completions";
    public static bool IsOfficialEndpoint(string endpoint) => endpoint is Endpoint or "https://api.deepseek.com/v1/chat/completions";
    public static Backend Preset(string id, string credentialRef, ProxyMode proxy = ProxyMode.Direct, string proxyAddress = "") =>
        new(id, "DeepSeek", Endpoint, "deepseek-flash", credentialRef, TimeoutSeconds: 300,
            ProxyMode: proxy, ProxyAddress: proxy == ProxyMode.Custom ? proxyAddress : "");

    public static IReadOnlyList<ModelOption> ParseModels(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 256) throw new SafeFailure("model_list_invalid");
        var models = new List<ModelOption>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/')))
                throw new SafeFailure("model_list_invalid");
            var images = item.TryGetProperty("input_modalities", out var modes) && modes.ValueKind == JsonValueKind.Array &&
                modes.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "image");
            if (models.All(x => x.Id != id)) models.Add(new(id, images));
        }
        if (models.Count == 0) throw new SafeFailure("model_list_empty");
        return models;
    }

    public static async Task<QuickSetupResult> ConnectAsync(IModelCatalog catalog, IBackendClient client, Backend draft,
        CancellationToken cancellationToken)
    {
        // Quick setup has a fixed, disclosed recipient. Custom providers use the advanced editor.
        if (draft.Endpoint != Endpoint) throw new SafeFailure("endpoint_invalid");
        Policy.ValidateBackend(draft);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(draft.TimeoutSeconds));
        var models = await catalog.ListModelsAsync(draft, timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        var chosen = models.FirstOrDefault(x => x.Id == "deepseek-flash") ?? models.FirstOrDefault(x => x.Id == "deepseek-chat") ??
            models.FirstOrDefault() ?? throw new SafeFailure("model_list_empty");
        var candidate = draft with { Model = chosen.Id, SupportsImages = chosen.SupportsImages };
        var result = await BackendProbe.RunAsync(client, candidate with { MaxTokens = 64 }, timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        if (!result.Success) throw new SafeFailure(result.Code);
        if (!result.ReceivedText) throw new SafeFailure("probe_empty_response");
        return new(candidate, models);
    }
}
