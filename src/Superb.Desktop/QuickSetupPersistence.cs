using System.Collections.Immutable;
using Superb.Core;
using Superb.Desktop.Platform;

namespace Superb.Desktop;

internal static class QuickSetupPersistence
{
    // Publish the new configuration only after both the credential and config are durable.
    // A failed save keeps the previous credential and removes the unreferenced new one.
    internal static Configuration Commit(Configuration current, Backend candidate, string secret,
        CredentialStore credentials, Action<Configuration> save)
    {
        var old = current.Backends.FirstOrDefault(x => x.Id == candidate.Id);
        var reference = credentials.Save(secret);
        Configuration updated;
        try
        {
            candidate = candidate with { CredentialRef = reference };
            updated = current with
            {
                Backends = current.Backends.Where(x => x.Id != candidate.Id).Append(candidate).ToImmutableArray(),
                SelectedBackendId = candidate.Id
            };
            updated.Validate();
            save(updated);
        }
        catch
        {
            credentials.Delete(reference);
            throw;
        }
        if (old is not null && old.CredentialRef.Length > 0 && !updated.Backends.Any(x => x.CredentialRef == old.CredentialRef))
        {
            try { credentials.Delete(old.CredentialRef); } catch { /* A durable configuration must not be rolled back after cleanup failure. */ }
        }
        return updated;
    }
}
