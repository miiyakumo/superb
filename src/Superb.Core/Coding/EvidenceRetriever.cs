using System.Text;

namespace Superb.Core.Coding;

public sealed record EvidenceMatch(EvidenceRef Reference, string Excerpt, int Score);
public sealed record ObservationComparison(Guid Left, Guid Right, bool Identical, int CommonPrefix,
    string LeftExcerpt, string RightExcerpt, bool Truncated,
    string Note = "Comparison of captured evidence only; capture order does not establish file freshness.");

public static class EvidenceRetriever
{
    public static IReadOnlyList<EvidenceMatch> Search(CodingSnapshot snapshot, string query, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000 || limit is < 1 or > 32)
            throw new SafeFailure("coding_search_invalid");
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
        var matches = new List<EvidenceMatch>();
        foreach (var observation in snapshot.Observations)
        {
            var text = observation.Snapshot.Content.Text ?? "";
            // Overlapping passages preserve symbols at chunk boundaries and provide usable source offsets.
            for (var start = 0; start < text.Length; start += 1200)
            {
                var length = Math.Min(1600, text.Length - start);
                var passage = text.Substring(start, length);
                var score = terms.Count(term => passage.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (score > 0) matches.Add(new(new(observation.Id, start, length), passage, score));
            }
        }
        return matches.OrderByDescending(m => m.Score).ThenBy(m => m.Reference.Start).Take(limit).ToArray();
    }

    public static string Read(CodingSnapshot snapshot, EvidenceRef reference)
    {
        if (reference is null) throw new SafeFailure("coding_evidence_invalid");
        var observation = snapshot.Observations.FirstOrDefault(o => o.Id == reference.ObservationId);
        if (observation is null) throw new SafeFailure("coding_evidence_invalid");
        var text = observation.Snapshot.Content.Text ?? "";
        if (reference.Start < 0 || reference.Length < 0 || reference.Start > text.Length ||
            reference.Length > text.Length - reference.Start ||
            (reference.Length == 0 && !(reference.Start == 0 && observation.Snapshot.Content.HasImage)))
            throw new SafeFailure("coding_evidence_invalid");
        return text.Substring(reference.Start, reference.Length);
    }

    public static ObservationComparison Compare(CodingSnapshot snapshot, Guid left, Guid right)
    {
        var l = snapshot.Observations.FirstOrDefault(o => o.Id == left);
        var r = snapshot.Observations.FirstOrDefault(o => o.Id == right);
        if (l is null || r is null) throw new SafeFailure("coding_evidence_invalid");
        var lt = l.Snapshot.Content.Text ?? "";
        var rt = r.Snapshot.Content.Text ?? "";
        var prefix = 0;
        while (prefix < Math.Min(lt.Length, rt.Length) && lt[prefix] == rt[prefix]) prefix++;
        var start = Math.Max(0, prefix - 300);
        var ll = Math.Min(8000, lt.Length - start);
        var rl = Math.Min(8000, rt.Length - start);
        return new(left, right, l.Hash == r.Hash, prefix, lt.Substring(start, ll), rt.Substring(start, rl),
            start + ll < lt.Length || start + rl < rt.Length);
    }
}
