using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Superb.Core.Coding;

public enum AgentProtocol { JsonActions, NativeTools }
public enum AttemptOutcome { Unknown, Claimed, Improved, Unchanged, Regressed, Verified, Incomparable }
public enum CodingRunState { Idle, Analyzing, Retrieving, Diagnosing, Reviewing, AwaitingFeedback, Failed, Cancelled }
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
public sealed record AgentAction(string Name, JsonElement Arguments);
public sealed record AgentModelRequest(Backend Backend, AgentProtocol Protocol, string SystemPrompt, Payload Content);
public interface IAgentModelClient
{
    Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken cancellationToken);
}
public sealed record CodingRunOptions(AgentProtocol Protocol = AgentProtocol.JsonActions, int MaximumCalls = 12,
    int MaximumSeconds = 300, int MaximumOutputChars = 180000);
public sealed record CodingRunUpdate(Guid RunId, CodingRunState State, int Calls, string Message, Directive? Directive = null);
public sealed record TechnicalAnalysis(string Conclusion, string ProposedChange, string Correctness,
    string Complexity, [property: JsonConverter(typeof(TextOrLinesConverter))] string Checks, ImmutableArray<EvidenceRef> Evidence);
public sealed record SolutionReview(bool Accepted, string Findings, ImmutableArray<EvidenceRef> Evidence);
public static class CodingJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Options) ?? throw new SafeFailure("coding_action_invalid");
}

// Explicit union for a list of verification checks, not a general coercion of model JSON.
public sealed class TextOrLinesConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected text or a list of text checks.");
        var lines = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String || lines.Count >= 64) throw new JsonException("Invalid check list.");
            lines.Add(reader.GetString()!);
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Incomplete check list.");
        return string.Join("\n", lines.Select(line => "- " + line));
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
