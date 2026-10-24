using System.Net.Http;
using System.Text.Json;

namespace Superb.Core;

// Never return Exception.Message, endpoint URLs, headers or remote error bodies.
public static class ConnectionDiagnostics
{
    public static string CodeFor(Exception error) => error switch
    {
        SafeFailure safe => safe.Code,
        HttpRequestException http => HttpCode(http.HttpRequestError),
        HttpIOException http => HttpCode(http.HttpRequestError),
        JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException => "response_format_invalid",
        System.Text.DecoderFallbackException => "response_encoding_invalid",
        IOException => "network_read_failed",
        OperationCanceledException => "request_timed_out",
        _ => "backend_request_failed"
    };

    private static string HttpCode(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => "network_dns_failed",
        HttpRequestError.ConnectionError => "network_connect_failed",
        HttpRequestError.SecureConnectionError => "network_tls_failed",
        HttpRequestError.ProxyTunnelError => "network_proxy_failed",
        HttpRequestError.UserAuthenticationError => "network_auth_failed",
        HttpRequestError.ResponseEnded => "stream_incomplete",
        HttpRequestError.ConfigurationLimitExceeded => "response_too_large",
        HttpRequestError.HttpProtocolError or HttpRequestError.VersionNegotiationError or HttpRequestError.InvalidResponse => "network_protocol_failed",
        _ => "network_request_failed"
    };

    public static string Explain(string? code) => code switch
    {
        "network_dns_failed" => "域名解析失败；检查端点、DNS，或选择可用的代理。",
        "network_connect_failed" => "无法建立连接；检查网络、防火墙及代理地址/端口。",
        "network_tls_failed" => "TLS 握手或证书验证失败；检查系统时间、证书及代理，不要关闭证书验证。",
        "network_proxy_failed" or "http_407" => "代理连接或认证失败；检查代理是否运行、端口及协议是否正确。",
        "network_auth_failed" => "连接认证失败；检查服务端或代理的认证要求。",
        "request_timed_out" or "request_timeout" => "请求超时；检查直连/代理、服务是否可达及超时设置。",
        "request_cancelled" => "测试已取消。",
        "http_400" or "http_422" => "后端拒绝请求参数；检查模型、图像能力、temperature 和 token 参数名称。",
        "http_401" => "后端拒绝认证；重新保存该端点对应的 API 密钥。",
        "http_402" => "账户余额不足；请在服务商账户充值后重试。",
        "credential_invalid" => "请填写有效的 API 密钥。",
        "backend_name_invalid" => "请填写配置名称，长度为 1–80 个字符。",
        "backend_model_required" => "请填写服务提供的模型 ID。",
        "backend_endpoint_required" => "请填写模型服务的 URL。",
        "backend_timeout_invalid" => "超时须为 1–600 之间的整数秒数。",
        "backend_tokens_invalid" => "生成 tokens 须为正整数，且不超过本地上限（通用 200000，DeepSeek 官方 393216）；服务可能要求更低的值。",
        "backend_temperature_invalid" => "温度须为 0–2 之间的数字，小数使用英文句点。",
        "model_list_empty" => "此账户没有可用模型；请检查模型权限。",
        "model_list_invalid" => "模型列表格式不正确；请在高级连接设置中手动配置。",
        "probe_empty_response" => "连接成功但未收到文本；请在高级连接设置中检查模型与生成参数。",
        "config_too_large" => "配置文件超过 1 MiB，请缩短提示词或减少场景后再保存。",
        "scene_invalid" => "场景配置无效；检查名称、系统提示词和模型连接。",
        "scene_backend_missing" => "场景指定的后端不存在，请重新选择并保存。",
        "scene_missing" => "场景已不存在，请重新选择。",
        "hotkey_duplicate" or "hotkey_action_duplicate" => "快捷键重复；请为每个已启用动作选择不同绑定。",
        "hotkey_invalid" => "快捷键格式无效；例如 Ctrl+Alt+C 或独占模式下的 F9。",
        "hotkey_reserved" => "该键或组合属于保留范围，请选择其他绑定。",
        "hotkey_exclusive_function_required" => "独占模式仅支持不带修饰键的 F1–F11、F13–F24。",
        "hotkey_combination_required" => "普通模式需要非保留的组合键，例如 Ctrl+Alt+C。",
        "hotkey_required_action_missing" => "快捷键动作缺失；请恢复默认后重新编辑。",
        "system_prompt_invalid" => "系统提示词过长或无效。",
        "input_empty" => "请先输入问题。",
        "backend_missing" => "请先到“连接模型”完成配置。",
        "capture_selection_unavailable" => "无法启动两点选择，请取消后重试。",
        "capture_cross_monitor" => "两个角需要位于同一显示器，请重新选择。",
        "capture_region_too_small" => "截图区域太小，请重新选择两个角。",
        "capture_monitor_lost" or "capture_geometry_changed" => "显示器范围已变化，请重新截图。",
        "capture_unavailable" or "capture_failed" => "屏幕采集失败，未加入队列。",
        "capture_black_frame" => "未采集到有效画面，未加入队列。",
        "image_queue_full" => "队列最多 5 张图片，请先移除或发送。",
        "input_queue_full" => "队列最多 10 项，请先移除或发送。",
        "input_too_large" or "image_too_large" => "输入总量超过限制，请移除部分内容后发送。",
        "image_dimensions_invalid" => "图片总像素超过限制，或图片尺寸无效；请缩小截图范围。",
        "http_403" => "后端拒绝访问；检查账号、地区、模型权限及网关规则。",
        "http_404" or "http_405" => "端点或模型不存在；填写完整 Chat Completions POST 地址，不能只填域名或 /v1。",
        "http_429" => "后端限流或额度不足；检查配额，不会自动重试。",
        "http_500" or "http_502" or "http_503" or "http_504" => "后端或网关暂时不可用；检查服务状态后手动重试。",
        "redirect_blocked" => "后端要求重定向；核对并显式保存最终的授权端点。",
        "credential_unavailable" => "无法读取当前用户的凭据；请在这台 Windows 设备上重新保存密钥。",
        "stream_response_expected" => "端点没有返回事件流；检查 API 路径，或关闭流式输出后再测试。",
        "stream_incomplete" or "stream_missing_finish" => "响应流未完整结束；检查代理缓冲、连接中断及后端的结束信号。",
        "response_format_invalid" or "response_encoding_invalid" => "响应不符合 Chat Completions 格式；检查是否误填网页、Responses 或其他协议端点。",
        "tool_calls_not_supported" => "普通问答收到工具调用；请使用 AI Coding 协作场景，或让后端返回普通回答。",
        "coding_call_budget_exhausted" => "本次分析已达到调用上限，未发布未完成指令；原始材料和已提交记忆保留。可调整预算后重新分析。",
        "coding_time_budget_exhausted" => "本次分析已达到总时限；已提交记忆保留，可重试或调整预算后重新分析。",
        "coding_output_budget_exhausted" => "本次累计输出达到上限，未提交本次结论；请缩小分析范围。",
        "coding_no_progress" => "连续检索没有取得新材料，已暂停并保留检查点；可重试继续提交指令，或补充材料后分析。",
        "coding_history_invalid" => "工具会话的调用与返回无法配对；检查后端是否返回唯一的原生调用 ID，已有检查点保留。",
        "coding_material_query_invalid" => "材料检索参数无效；请缩小批量范围或使用准确的来源和分页。",
        "coding_action_invalid" or "coding_action_repair_exhausted" => "模型未能提交有效工具参数或执行指令；检查后端是否支持原生工具调用，或更换模型后重新分析。",
        "coding_tool_call_required" => "后端未返回完整的原生工具调用；请使用支持工具调用的模型和接口。",
        "coding_context_too_large" => "本次所需上下文超过限制，未截断关键材料或提交本次结论；请缩小本次问题范围。",
        "coding_evidence_limit" or "coding_memory_limit" => "当前任务记忆已达到容量上限；已有材料保留，需要开始新任务再继续。",
        "coding_run_stale" or "coding_retry_stale" => "任务记忆已改变，旧分析不能提交；请使用当前材料重新分析。",
        "coding_retry_unavailable" => "没有可重试的未完成分析；请带回新材料继续。",
        "coding_budget_invalid" => "任务分析参数无效；调用次数可留空表示不限，或填 1–10000；总时限须为 10–1800 秒。",
        "coding_feedback_unproven" or "coding_execution_unproven" or "coding_completion_unproven" => "缺少可关联的执行或验证证据，未将平台自述标记为成功。",
        "response_not_complete" => "后端未正常完成回答；检查输出 token 上限或内容过滤。",
        "response_token_limit" => "本次输出 token 已用尽（思考也可能占用额度）；在模型连接设置提高上限后重新发送。",
        "response_content_filtered" => "后端内容过滤中止了回答；未将部分内容标为完成。",
        "response_interrupted" => "后端中断了生成；可手动重新回答，不会自动重试。",
        "response_empty" => "后端结束了请求，但没有返回回答正文；未标为成功，可检查模型设置后重新发送。",
        "thinking_mode_unsupported" => "思考模式开关仅适用于 DeepSeek 官方端点；其他后端请选择“沿用模型默认”。",
        "proxy_address_invalid" => "代理地址无效；使用 http://主机:端口、https://主机:端口 或 socks5://主机:端口，不附带密码。",
        "endpoint_invalid" or "encrypted_transport_required" => "检查完整 HTTPS 端点；仅显式授权的 IP 回环端点可使用 HTTP。",
        "backend_has_no_vision" => "当前模型连接不支持图片，请更换支持图像的模型连接后发送。",
        _ => "请检查已保存的后端、协议和网络配置；错误详情不会包含正文或密钥。"
    };
    public static string Describe(string? code) => $"{code ?? "backend_request_failed"} · {Explain(code)}";
}

public sealed record ProbeResult(bool Success, string Code, bool HttpAccepted, bool ReceivedText);
public static class BackendProbe
{
    public const string TestPrompt = "Reply with OK.";
    public static async Task<ProbeResult> RunAsync(IBackendClient client, Backend backend, CancellationToken cancellationToken)
    {
        var accepted = false;
        var received = false;
        try
        {
            Policy.ValidateBackend(backend);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(backend.TimeoutSeconds));
            var payload = Payload.FromText(TestPrompt);
            var snapshot = new InputSnapshot(Guid.NewGuid(), InputKind.ClipboardText, payload, 0, DateTimeOffset.UtcNow);
            var request = new PreparedRequest(snapshot, payload, backend);
            var length = 0L;
            await foreach (var item in client.SendAsync(request, timeout.Token))
            {
                timeout.Token.ThrowIfCancellationRequested();
                accepted |= item.Waiting;
                received |= !string.IsNullOrEmpty(item.Delta);
                length += item.Delta?.Length ?? 0;
                if (length > backend.MaxOutputChars) throw new SafeFailure("output_too_large");
                if (item.Completed) return new(true, "completed", accepted, received);
            }
            return new(false, "stream_incomplete", accepted, received);
        }
        catch (OperationCanceledException) { return new(false, cancellationToken.IsCancellationRequested ? "request_cancelled" : "request_timed_out", accepted, received); }
        catch (Exception ex) { return new(false, ConnectionDiagnostics.CodeFor(ex), accepted, received); }
    }
}
