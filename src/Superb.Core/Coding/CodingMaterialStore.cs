using System.Collections.Immutable;

namespace Superb.Core.Coding;

internal sealed record MaterialPassage(EvidenceRef Reference, string Text, int TotalTextLength, bool HasImage);
internal sealed record MaterialPacket(ImmutableArray<MaterialPassage> Passages, int Matches, bool More);

internal static class CodingMaterialStore
{
    internal static MaterialPacket Retrieve(CodingSnapshot snapshot, MaterialQuery query)
    {
        if (query.Query is null || query.Query.Length > 2000 || query.Offset is < 0 or > 1000000 || query.Limit is < 1 or > 8 ||
            (!query.Ranges.IsDefault && query.Ranges.Length > 8) || (!query.Ranges.IsDefaultOrEmpty && query.Query.Length > 0))
            throw new SafeFailure("coding_material_query_invalid");
        IEnumerable<EvidenceRef> references;
        int count;
        if (!query.Ranges.IsDefaultOrEmpty)
        {
            if (query.Ranges.Any(r => r is null || r.Length > 32000)) throw new SafeFailure("coding_evidence_read_too_large");
            references = query.Ranges;
            count = query.Ranges.Length;
        }
        else if (!string.IsNullOrWhiteSpace(query.Query))
        {
            var matches = EvidenceRetriever.Search(snapshot, query.Query, int.MaxValue);
            count = matches.Count;
            references = matches.Skip(query.Offset).Take(query.Limit).Select(m => m.Reference);
        }
        else
        {
            count = snapshot.Observations.Length;
            references = snapshot.Observations.Reverse().Skip(query.Offset).Take(query.Limit)
                .Select(o => new EvidenceRef(o.Id, 0, Math.Min(Math.Min(12000, 32000 / query.Limit), o.Snapshot.Content.Text?.Length ?? 0)));
        }
        var passages = references.Select(reference =>
        {
            var text = EvidenceRetriever.Read(snapshot, reference);
            var source = snapshot.Observations.First(o => o.Id == reference.ObservationId);
            return new MaterialPassage(reference, text, source.Snapshot.Content.Text?.Length ?? 0, source.Snapshot.Content.HasImage);
        }).ToImmutableArray();
        if (passages.Sum(p => p.Text.Length) > 32000) throw new SafeFailure("coding_evidence_read_too_large");
        return new(passages, count, query.Ranges.IsDefaultOrEmpty && query.Offset + passages.Length < count);
    }
}
