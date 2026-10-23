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
        "request_timed_out" => "请求超时；检查直连/代理、服务是否可达及超时设置。",
        "request_cancelled" => "测试已取消。",
        "http_400" or "http_422" => "后端拒绝请求参数；检查模型、图像能力、temperature 和 token 参数名称。",
        "http_401" => "后端拒绝认证；重新保存该端点对应的 API 密钥。",
        "http_402" => "账户余额不足；请在服务商账户充值后重试。",
        "credential_invalid" => "请填写有效的 API 密钥。",
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
        "tool_calls_not_supported" => "后端返回了工具调用；当前只支持文本或图像问答。",
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
