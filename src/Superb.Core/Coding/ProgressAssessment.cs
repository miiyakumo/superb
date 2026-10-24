using System.Collections.Immutable;

namespace Superb.Core.Coding;

public static class ProgressAssessment
{
    public static ProgressDecision Evaluate(TaskMemory memory)
    {
        var failures = new Dictionary<(string Issue, string Strategy), List<AttemptAssessment>>();
        foreach (var attempt in memory.Attempts)
        {
            var assessment = attempt.Assessment;
            if (assessment is null || !assessment.ExecutionObserved || !assessment.Comparable || assessment.Evidence.IsDefaultOrEmpty ||
                assessment.FeedbackObservationIds.IsDefaultOrEmpty)
                continue;
            var issue = Normalize(assessment.IssueKey);
            if (assessment.Outcome is AttemptOutcome.Improved or AttemptOutcome.Verified)
            {
                foreach (var key in failures.Keys.Where(k => k.Issue == issue).ToArray()) failures.Remove(key);
                continue;
            }
            if (assessment.Outcome is not (AttemptOutcome.Unchanged or AttemptOutcome.Regressed)) continue;
            var strategy = Normalize(assessment.Strategy);
            var keyForAttempt = (issue, strategy);
            if (!failures.TryGetValue(keyForAttempt, out var group)) failures[keyForAttempt] = group = [];
            // One returned observation cannot prove two independent failed executions.
            if (group.Any(prior => prior.FeedbackObservationIds.Intersect(assessment.FeedbackObservationIds).Any()))
                continue;
            group.Add(assessment);
        }
        var regression = failures.Values.SelectMany(g => g).LastOrDefault(a => a.Outcome == AttemptOutcome.Regressed);
        if (regression is not null)
        {
            var group = failures[(Normalize(regression.IssueKey), Normalize(regression.Strategy))];
            return new(true, $"Execution evidence shows regression for {regression.IssueKey}; re-examine the diagnosis before another edit.",
                group.Count, FailureIds(group));
        }
        var repeated = failures.Values.Where(g => g.Count >= 2).OrderByDescending(g => g.Count).FirstOrDefault();
        if (repeated is not null)
            return new(true, $"{repeated.Count} comparable, independently evidenced failures for {repeated[0].IssueKey} using {repeated[0].Strategy}; analyze the bottleneck and reconsider the strategy.",
                repeated.Count, FailureIds(repeated));
        return new(false, "No repeated comparable failure established; assess current evidence and technical constraints.",
            failures.Values.Select(g => g.Count).DefaultIfEmpty(0).Max());
    }

    private static ImmutableArray<Guid> FailureIds(IEnumerable<AttemptAssessment> failures) =>
        failures.SelectMany(a => a.FeedbackObservationIds).Distinct().ToImmutableArray();

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
