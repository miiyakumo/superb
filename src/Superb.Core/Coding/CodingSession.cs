using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Superb.Core.Coding;

/// <summary>Task-local evidence and committed memory. Capture order never implies a repository revision.</summary>
public sealed class CodingSession
{
    public const long MaximumObservationBytes = 32 * 1024 * 1024;
    public const int MaximumObservations = 1000;
    public const int MaximumFacts = 512;
    public const int MaximumAttempts = 256;
    private readonly object gate = new();
    private Guid id = Guid.NewGuid();
    private long epoch;
    private long bytes;
    private ImmutableArray<Observation> observations = [];
    private TaskMemory memory = new(0, [], []);

    public Guid Id { get { lock (gate) return id; } }
    public TaskMemory Memory { get { lock (gate) return memory; } }
    public IReadOnlyList<Observation> Observations { get { lock (gate) return observations; } }
    public Directive? CurrentDirective { get { lock (gate) return memory.Attempts.LastOrDefault()?.Directive; } }

    public Observation AddObservation(InputSnapshot snapshot) => AddObservations([snapshot])[0];

    public ImmutableArray<Observation> AddObservations(IEnumerable<InputSnapshot> snapshots)
    {
        lock (gate)
        {
            var next = observations;
            var nextBytes = bytes;
            var added = ImmutableArray.CreateBuilder<Observation>();
            foreach (var snapshot in snapshots)
            {
                if (snapshot is null || snapshot.Content is null || snapshot.Content.Size == 0 ||
                    (!snapshot.Content.HasImage && string.IsNullOrWhiteSpace(snapshot.Content.Text)))
                    throw new SafeFailure("input_empty");
                var hash = ContentHash(snapshot.Content);
                var duplicate = next.FirstOrDefault(o => o.Hash == hash);
                if (duplicate is not null) { added.Add(duplicate); continue; }
                if (next.Length >= MaximumObservations || snapshot.Content.Size > MaximumObservationBytes - nextBytes)
                    throw new SafeFailure("coding_evidence_limit");
                var observation = new Observation(Guid.NewGuid(), snapshot, hash);
                next = next.Add(observation);
                nextBytes += snapshot.Content.Size;
                added.Add(observation);
            }
            observations = next;
            bytes = nextBytes;
            return added.ToImmutable();
        }
    }

    public CodingSnapshot Freeze()
    {
        lock (gate) return new(id, epoch, memory, observations);
    }

    public ActionAttempt Commit(CodingSnapshot basis, MemoryChanges changes, Directive directive)
    {
        lock (gate)
        {
            if (basis.SessionId != id || basis.Epoch != epoch || basis.Memory.Revision != memory.Revision)
                throw new SafeFailure("coding_run_stale");
            ValidateChanges(basis, changes, directive);
            var facts = memory.Facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
            foreach (var fact in changes.Facts) facts[fact.Id] = fact;
            var assessments = changes.Assessments.ToDictionary(a => a.AttemptId);
            var attempts = memory.Attempts.Select(a => assessments.TryGetValue(a.Id, out var assessment)
                ? a with { Assessment = assessment } : a).ToImmutableArray();
            if (attempts.Length >= MaximumAttempts) throw new SafeFailure("coding_memory_limit");
            var attempt = new ActionAttempt(Guid.NewGuid(), directive);
            memory = new(memory.Revision + 1, facts.Values.ToImmutableArray(), attempts.Add(attempt));
            return attempt;
        }
    }

    public void MarkCopied(Guid attemptId)
    {
        lock (gate)
        {
            var index = -1;
            for (var i = 0; i < memory.Attempts.Length; i++)
                if (memory.Attempts[i].Id == attemptId) { index = i; break; }
            if (index < 0) throw new SafeFailure("coding_attempt_unknown");
            if (memory.Attempts[index].Copied) return;
            // Transfer status is not a new reasoning revision. Commit merges from live memory and preserves it.
            memory = memory with { Attempts = memory.Attempts.SetItem(index, memory.Attempts[index] with { Copied = true }) };
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            id = Guid.NewGuid();
            epoch++;
            bytes = 0;
            observations = [];
            memory = new(0, [], []);
        }
    }

    public static void ValidateChanges(CodingSnapshot basis, MemoryChanges changes, Directive? directive = null)
    {
        if (changes is null || changes.Facts.IsDefault || changes.Assessments.IsDefault || changes.Facts.Length > MaximumFacts ||
            changes.Assessments.Length > MaximumAttempts) throw new SafeFailure("coding_memory_invalid");
        var factIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in changes.Facts)
        {
            if (fact is null) throw new SafeFailure("coding_memory_invalid");
            RequireText(fact.Id, 128); RequireText(fact.Kind, 80); RequireText(fact.Text, 12000);
            if (!factIds.Add(fact.Id)) throw new SafeFailure("coding_memory_invalid");
            ValidateEvidence(basis, fact.Evidence, required: true);
        }
        if (basis.Memory.Facts.Select(f => f.Id).Union(factIds, StringComparer.Ordinal).Count() > MaximumFacts)
            throw new SafeFailure("coding_memory_limit");
        var assessmentIds = new HashSet<Guid>();
        foreach (var assessment in changes.Assessments)
        {
            if (assessment is null || !Enum.IsDefined(assessment.Outcome) || !assessmentIds.Add(assessment.AttemptId) ||
                !basis.Memory.Attempts.Any(a => a.Id == assessment.AttemptId)) throw new SafeFailure("coding_assessment_invalid");
            RequireText(assessment.IssueKey, 256); RequireText(assessment.Strategy, 1000);
            var actualOutcome = assessment.Outcome is AttemptOutcome.Improved or AttemptOutcome.Unchanged
                or AttemptOutcome.Regressed or AttemptOutcome.Verified;
            if (actualOutcome && !assessment.ExecutionObserved) throw new SafeFailure("coding_execution_unproven");
            ValidateEvidence(basis, assessment.Evidence,
                assessment.ExecutionObserved || assessment.Outcome != AttemptOutcome.Unknown);
            if (actualOutcome && assessment.FeedbackObservationIds.IsDefaultOrEmpty)
                throw new SafeFailure("coding_feedback_unproven");
            if (!assessment.FeedbackObservationIds.IsDefault)
            {
                if (assessment.FeedbackObservationIds.Length > 128 ||
                    assessment.FeedbackObservationIds.Distinct().Count() != assessment.FeedbackObservationIds.Length ||
                    assessment.FeedbackObservationIds.Any(id => !assessment.Evidence.Any(e => e.ObservationId == id)))
                    throw new SafeFailure("coding_feedback_unproven");
            }
        }
        if (directive is null) return;
        RequireText(directive.Direction, 4000); RequireText(directive.Prompt, 100000);
        RequireText(directive.IssueKey, 256); RequireText(directive.Strategy, 1000);
        RequireText(directive.ExpectedEffect, 4000);
        RequireText(directive.BeforeSend, 4000, allowEmpty: true);
        RequireText(directive.ReturnEvidence, 12000, allowEmpty: directive.TaskComplete);
        ValidateEvidence(basis, directive.Evidence, required: true);
        if (directive.TaskComplete)
        {
            // A historical partial success cannot certify the latest execution or the whole task.
            // Completion requires a fresh assessment of the latest issued directive in this commit.
            var latestAttempt = basis.Memory.Attempts.LastOrDefault();
            var latestAssessment = latestAttempt is null ? null :
                changes.Assessments.FirstOrDefault(a => a.AttemptId == latestAttempt.Id);
            if (latestAssessment is not { Outcome: AttemptOutcome.Verified, ExecutionObserved: true } ||
                latestAssessment.FeedbackObservationIds.IsDefaultOrEmpty)
                throw new SafeFailure("coding_completion_unproven");
        }
    }

    internal static void ValidateEvidence(CodingSnapshot basis, ImmutableArray<EvidenceRef> evidence, bool required)
    {
        if (evidence.IsDefault || evidence.Length > 128 || (required && evidence.IsEmpty))
            throw new SafeFailure("coding_evidence_invalid");
        foreach (var reference in evidence) EvidenceRetriever.Read(basis, reference);
    }

    private static void RequireText(string? value, int maximum, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new SafeFailure("coding_memory_invalid");
    }

    private static string ContentHash(Payload payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var text = Encoding.UTF8.GetBytes(payload.Text ?? "");
        hash.AppendData(BitConverter.GetBytes(text.Length)); hash.AppendData(text);
        foreach (var image in payload.Images)
        {
            hash.AppendData(BitConverter.GetBytes(image.Width)); hash.AppendData(BitConverter.GetBytes(image.Height));
            hash.AppendData(BitConverter.GetBytes(image.Png.Length)); hash.AppendData(image.Png.AsSpan());
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
