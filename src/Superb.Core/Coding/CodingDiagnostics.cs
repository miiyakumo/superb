namespace Superb.Core.Coding;

public enum CodingDiagnosticKind { Started, Phase, CallStarted, CallCompleted, CallFailed, ToolCompleted, Repair, Completed, Failed, Cancelled, Transport, Resumed, Paused }
public enum CodingTool { RetrieveMaterial, SubmitResult, Unknown }
public sealed record CodingStepMetrics(int MessageCount, long ContextCharacters, int RetrievedPassages = 0,
    bool Cached = false, int NoProgressSteps = 0, bool CheckpointResumed = false);

// Deliberately has no fields for model/input text, endpoint, credentials, tool arguments or results.
public sealed record CodingDiagnosticEvent(Guid RunId, long Sequence, DateTimeOffset TimestampUtc, CodingDiagnosticKind Event,
    CodingRunState Phase, int ModelCalls, long ElapsedMilliseconds, long? CallElapsedMilliseconds = null,
    CodingTool? Tool = null, string? ErrorCode = null, CodingTransportSnapshot? Transport = null, CodingStepMetrics? Step = null);

public static class CodingDiagnostics
{
    public static CodingTool Tool(string name) => name switch
    {
        "retrieve_material" => CodingTool.RetrieveMaterial, "submit_result" => CodingTool.SubmitResult, _ => CodingTool.Unknown
    };
    public static string ToolLabel(CodingTool tool) => tool switch
    {
        CodingTool.RetrieveMaterial => "检索任务材料", CodingTool.SubmitResult => "提交执行指令", _ => "处理模型响应"
    };
    // Unknown codes are never persisted: even a malicious exception cannot smuggle text into logs.
    public static string? SafeCode(string? code)
    {
        if (code is null) return null;
        if (code.Length == 8 && code.StartsWith("http_", StringComparison.Ordinal) &&
            int.TryParse(code.AsSpan(5), out var status) && status is >= 100 and <= 599) return code;
        return code switch
        {
            "coding_busy" or "coding_budget_invalid" or "coding_call_budget_exhausted" or "coding_time_budget_exhausted" or
            "coding_output_budget_exhausted" or "coding_no_progress" or "coding_history_invalid" or "coding_material_query_invalid" or "coding_action_invalid" or "coding_response_invalid" or
            "coding_action_repair_exhausted" or "coding_tool_call_required" or "coding_remote_error" or "coding_action_unknown" or
            "coding_evidence_required" or "coding_evidence_invalid" or "coding_evidence_missing" or "coding_evidence_read_too_large" or
            "coding_directive_invalid" or "coding_memory_invalid" or "coding_memory_limit" or
            "coding_assessment_invalid" or "coding_search_invalid" or
            "coding_execution_unproven" or "coding_completion_unproven" or "coding_feedback_unproven" or
            "coding_context_too_large" or "coding_run_stale" or "coding_retry_stale" or "coding_retry_unavailable" or
            "coding_evidence_limit" or "coding_attempt_unknown" or "backend_missing" or "scene_missing" or "scene_backend_missing" or
            "capture_failed" or "capture_unavailable" or "capture_selection_unavailable" or "capture_timed_out" or
            "capture_cross_monitor" or "capture_region_too_small" or "capture_monitor_lost" or "capture_geometry_changed" or
            "image_queue_full" or "image_dimensions_invalid" or "input_empty" or "input_too_large" or "system_prompt_invalid" or
            "output_too_large" or "response_too_large" or "response_not_complete" or "response_token_limit" or
            "response_content_filtered" or "response_interrupted" or "response_empty" or "response_format_invalid" or
            "response_encoding_invalid" or "request_timeout" or "request_timed_out" or "request_cancelled" or
            "network_error" or "network_dns_failed" or "network_connect_failed" or "network_tls_failed" or
            "network_proxy_failed" or "network_auth_failed" or "network_read_failed" or "network_protocol_failed" or
            "network_request_failed" or "redirect_blocked" or "credential_invalid" or "credential_unavailable" or
            "endpoint_invalid" or "encrypted_transport_required" or "backend_limits_invalid" or "backend_has_no_vision" or
            "thinking_mode_unsupported" or "proxy_address_invalid" or "proxy_mode_invalid" or "adapter_unsupported" or
            "stream_incomplete" or "backend_request_failed" => code,
            _ => "backend_request_failed"
        };
    }
}
