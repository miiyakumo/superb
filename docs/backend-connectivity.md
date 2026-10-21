# 后端连接排查与修复（0.1.1）

日期：2026-10-21。排查基线：`2aad468`（已拉取最新 main）。用户允许使用代理，但未提供具体端点、错误码或真实凭据，所以本次可以确认代码缺陷和测试结果，不能宣称已连接用户的实际服务。

## 已确认的缺陷

1. `SocketsHttpHandler.UseProxy` 硬编码为 `false`。即使 Windows/环境变量已配置代理，应用仍直连；当网络只能通过代理访问模型时，请求失败。旧版本没有代理选择入口。
2. 协调器把 DNS、连接拒绝、TLS 和代理错误都转成 `backend_request_failed`，掩盖了排查方向。新增回归测试在修改前明确失败：预期 `network_dns_failed`，实际为通用错误。
3. SSE 解析只判断 `tool_calls` / `function_call` 字段是否存在，兼容服务返回 `null` 时也被错误判为工具调用。修改前新增测试明确复现 `tool_calls_not_supported`。空数组同样不包含工具调用，现已接受；有实际工具内容仍拒绝。

另外，原实现总是发送 `temperature` 和 `max_tokens`；特定模型不支持这些参数时会返回 400/422。现提供关闭 temperature 和改用 max_completion_tokens 的配置。这是兼容性选项，不是对未知用户模型根因的断言。

## 配置步骤

0.2.0 的入口已移至“高级设置 → 模型与连接”。DeepSeek 首次使用可直接在“连接模型”填写密钥并测试保存。下方记录保留 0.1.1 排障时点的结果；后续已用用户临时凭据在 Linux 上验证 DeepSeek 官方接口的流式和非流式调用，未将凭据保存到仓库。

1. **完整端点**：填写服务商给出的 Chat Completions POST 地址，例如 `https://服务域名/v1/chat/completions`。只填域名、`/v1`、网页地址或 `/v1/responses` 不适用于此适配器。不会自动补路径或跟随重定向。
2. **连接方式**：
   - 直连：明确禁用代理，保持旧配置行为。
   - 系统代理：使用 .NET 默认代理；Windows 优先读取代理环境变量，否则读取当前用户代理设置。系统设置在 .NET 进程中可能缓存，修改后建议重启 Superb。`NO_PROXY` 中的目标会绕过代理。
   - 自定义代理：填写实际监听地址，例如 `http://127.0.0.1:7890` 或 `socks5://127.0.0.1:7891`。这些端口只是示例，应与代理软件一致。仅支持无 URL 用户名/密码的 HTTP、HTTPS、SOCKS5 地址；不能把路径、密钥放在代理 URL 中。
3. **保存后端**，再点击 **测试已保存后端**。测试使用已保存配置，编辑框的未保存内容不会生效。它发送固定文本 `Reply with OK.`，使用真实模型生成参数，可能计费；不读取截图/剪贴板、不把回答显示或写回剪贴板。可取消，暂停/锁屏/退出/配置变更也会取消测试并抑制迟到结果。
4. 若 HTTP 400/422，根据模型文档关闭 temperature 或选择 max_completion_tokens；一次只改一项再测，避免把参数拒绝误当成网络不通。

代理选择及参数在每次请求中冻结，不会失败后自动切换。HTTPS 证书校验继续启用，模型 Bearer 凭据只发送给端点，不附在 HTTP CONNECT 代理握手上。系统代理模式遵循系统规则；企业代理认证未提供额外凭据界面。

## 错误码与检查方向

| 状态 | 含义 / 下一步 |
| --- | --- |
| `network_dns_failed` | DNS 解析失败；检查域名和 DNS / 代理 |
| `network_connect_failed` | TCP 连接失败；检查代理是否启动、端口、防火墙及目标可达性 |
| `network_tls_failed` | TLS/证书失败；检查系统时间、证书、代理；不关闭验证 |
| `network_proxy_failed` / `http_407` | 代理隧道或认证失败 |
| `request_timed_out` | 超时，检查网络/代理/模型生成耗时 |
| `credential_unavailable` | DPAPI 凭据不属于当前用户或文件缺失；重新保存密钥 |
| `http_401` / `http_403` | 密钥、账号、地区或模型访问权限问题 |
| `http_404` / `http_405` | 请求路径、模型 ID 或 HTTP 方法不匹配 |
| `http_400` / `http_422` | 模型或参数不兼容；检查图像、temperature、token 参数 |
| `http_429` | 限流或额度问题，无自动重试 |
| `stream_response_expected` | 要求流式，但 HTTP 200 返回 JSON/HTML；检查端点或关闭流式 |
| `response_format_invalid` | 数据不是此适配器要求的 Chat Completions 格式 |
| `stream_incomplete` / `stream_missing_finish` | 断流或结束协议不完整；不会误标成功 |
| `response_not_complete` | token 上限或其他非正常结束原因 |
| `session_not_ready` / 界面提示暂停、保护不可用 | 请求还没有到传输层；先处理会话/显示保护问题 |

诊断仅返回固定错误码、阶段与说明，不展示异常 Message、远端错误正文、私有 URL、请求正文或密钥。

## 验证方式

- 修改前复现：原有 25 项通过，新增“空工具字段”和“DNS 分类”两项失败。
- 回归覆盖：真实本机 HTTP 直连/系统代理/自定义代理、子进程读取 HTTP_PROXY、HTTPS CONNECT 407、真实自签名证书拒绝、参数序列化、断流、取消、固定测试文本等。全部使用合成数据；没有访问真实模型。
- Windows 专用覆盖：旧配置默认值兼容、配置 JSON 往返、DPAPI 加解密后的密钥通过真实 HTTP 代理请求完成认证、像素处理。
- CI：`.github/workflows/build.yml` 使用 `windows-2025`，记录 OS 构建、架构及 SDK；测试记录上传为 `windows-backend-diagnostics`，应用上传为 `superb-windows-x64-development`。
- 交互保护、Windows 11 桌面 UI、实际供应商及用户本地代理配置仍需用户设备实测；Windows Server CI 结果不能替代这些结论。

平台语义依据：[UseProxy](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.useproxy?view=net-10.0)、[HttpClient.DefaultProxy](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-10.0)、[HttpRequestError](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httprequesterror?view=net-10.0)。
