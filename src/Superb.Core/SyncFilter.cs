using System.Security.Cryptography;
using System.Text;

namespace Superb.Core;

// Called on the serialized application event loop; holds hashes, never clipboard bodies.
public sealed class SyncFilter
{
    private string? lastHash;
    private uint? lastSequence;
    public void Reset() { lastHash = null; lastSequence = null; }
    public bool Accept(InputSnapshot snapshot, bool ownOutput)
    {
        if (ownOutput || lastSequence == snapshot.ClipboardSequence) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(snapshot.Content.Text ?? ""));
        hash.AppendData([0]);
        foreach (var image in snapshot.Content.Images)
        { hash.AppendData(BitConverter.GetBytes(image.Png.Length)); hash.AppendData(image.Png.AsSpan()); }
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        lastSequence = snapshot.ClipboardSequence;
        if (lastHash == digest) return false;
        lastHash = digest;
        return true;
    }
}
