using System.Text.Json;

namespace Superb.Core.Coding;

public static class CodingTools
{
    private static object Text => new { type = "string" };
    private static object Uuid => new { type = "string", format = "uuid" };
    private static object Bool => new { type = "boolean" };
    private static object Number(int min, int max) => new { type = "integer", minimum = min, maximum = max };
    private static object Array(object items) => new { type = "array", items };
    private static object Object(params (string Name, object Schema, bool Required)[] fields) => new
    {
        type = "object", properties = fields.ToDictionary(f => f.Name, f => f.Schema),
        required = fields.Where(f => f.Required).Select(f => f.Name).ToArray(), additionalProperties = false
    };
    private static object Evidence => Object(("observationId", Uuid, true), ("start", Number(0, int.MaxValue), true), ("length", Number(0, 32000), true));
    public static bool Contains(string name) => name is "retrieve_material" or "submit_result";
    public static JsonElement Definitions()
    {
        var fact = Object(("id", Text, true), ("kind", Text, true), ("text", Text, true), ("evidence", Array(Evidence), true));
        var assessment = Object(("attemptId", Uuid, true), ("issueKey", Text, true), ("strategy", Text, true),
            ("outcome", new { type = "string", @enum = Enum.GetNames<AttemptOutcome>() }, true),
            ("comparable", Bool, true), ("executionObserved", Bool, true), ("evidence", Array(Evidence), true),
            ("feedbackObservationIds", Array(Uuid), false));
        var directive = Object(("direction", Text, true), ("beforeSend", Text, true), ("prompt", Text, true),
            ("returnEvidence", Text, true), ("issueKey", Text, true), ("strategy", Text, true), ("expectedEffect", Text, true),
            ("evidence", Array(Evidence), true), ("taskComplete", Bool, false));
        object Tool(string name, string description, object parameters) => new { type = "function", function = new { name, description, parameters } };
        return JsonSerializer.SerializeToElement(new[]
        {
            Tool("retrieve_material", "Retrieve original material from this task. Search with query and offset, or supply exact evidence ranges in one batch. No filesystem access. Already visible material need not be retrieved again.",
                Object(("query", Text, false), ("ranges", Array(Evidence), false), ("offset", Number(0, 1000000), false), ("limit", Number(1, 8), false))),
            Tool("submit_result", "Submit one self-contained instruction for the platform AI, or request specific missing material. Optional memory updates are committed together after evidence validation. This completes the turn.",
                Object(("directive", directive, true), ("memory", Object(("facts", Array(fact), false), ("assessments", Array(assessment), false)), false)))
        });
    }
}
