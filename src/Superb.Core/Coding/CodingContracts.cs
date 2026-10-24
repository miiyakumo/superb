using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Superb.Core.Coding;

public enum AttemptOutcome { Unknown, Claimed, Improved, Unchanged, Regressed, Verified, Incomparable }
public enum CodingRunState { Idle, Analyzing, Retrieving, AwaitingFeedback, Paused, Failed, Cancelled }
public sealed record EvidenceRef(Guid ObservationId, int Start, int Length);
public sealed record Observation(Guid Id, InputSnapshot Snapshot, string Hash);
public sealed record MemoryFact(string Id, string Kind, string Text, ImmutableArray<EvidenceRef> Evidence);
public sealed record AttemptAssessment(Guid AttemptId, string IssueKey, string Strategy, AttemptOutcome Outcome,
    bool Comparable, bool ExecutionObserved, ImmutableArray<EvidenceRef> Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<Guid> FeedbackObservationIds = default);
public sealed record Directive(string Direction, string BeforeSend, string Prompt, string ReturnEvidence,
    string IssueKey, string Strategy, string ExpectedEffect, ImmutableArray<EvidenceRef> Evidence,
    bool TaskComplete = false);
public sealed record ActionAttempt(Guid Id, Directive Directive, AttemptAssessment? Assessment = null, bool Copied = false);
public sealed record TaskMemory(long Revision, ImmutableArray<MemoryFact> Facts, ImmutableArray<ActionAttempt> Attempts);
public sealed record CodingSnapshot(Guid SessionId, long Epoch, TaskMemory Memory, ImmutableArray<Observation> Observations);
public sealed record MemoryChanges(ImmutableArray<MemoryFact> Facts, ImmutableArray<AttemptAssessment> Assessments);
public sealed record ProgressDecision(bool RequiresDeepAnalysis, string Reason, int ComparableFailures,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<Guid> FailureEvidenceIds = default);
public sealed record CodingSubmission(Directive Directive, MemoryChanges? Memory = null);
public sealed record MaterialQuery(string Query = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<EvidenceRef> Ranges = default,
    int Offset = 0, int Limit = 4);
public sealed record AgentAction(string CallId, string Name, string Arguments, string? Commentary = null,
    [property: JsonIgnore] string? ReasoningContent = null);
public enum AgentRole { User, Assistant, Tool }
public sealed record AgentMessage(AgentRole Role, Payload Content, AgentAction? Call = null, string? ToolCallId = null);
public sealed record AgentModelRequest(Backend Backend, string SystemPrompt, ImmutableArray<AgentMessage> Messages,
    [property: JsonIgnore] CodingTransportTrace? Transport = null);
public interface IAgentModelClient
{
    Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken cancellationToken);
}
public sealed record CodingRunOptions(int? MaximumCalls = null,
    int MaximumSeconds = 300, int MaximumOutputChars = 180000)
{
    public void Validate()
    {
        if (MaximumCalls is < 1 or > 10000 || MaximumSeconds is < 10 or > 1800 ||
            MaximumOutputChars is < 1024 or > 1000000) throw new SafeFailure("coding_budget_invalid");
    }
}
public sealed record CodingRunUpdate(Guid RunId, CodingRunState State, int Calls, string Message, Directive? Directive = null,
    long ElapsedMilliseconds = 0, bool WaitingForModel = false, long CallElapsedMilliseconds = 0, string? ErrorCode = null,
    CodingTransportSnapshot? Transport = null);
public static class CodingJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Options) ?? throw new SafeFailure("coding_action_invalid");
    public static T Read<T>(string value)
    {
        using var document = JsonDocument.Parse(value);
        CheckUniqueProperties(document.RootElement);
        return Read<T>(document.RootElement);
    }
    internal static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new SafeFailure("coding_action_invalid");
                CheckUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUniqueProperties(item);
    }
}
