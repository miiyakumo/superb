# 架构与需求映射

评审基线：2026-10-16，0.1 开发实现。该文档描述代码与验证缺口，不代表需求已全部验收。

## 分层与信任边界

```mermaid
flowchart LR
  User[用户动作 / 限时授权] --> Input[Win32 采集与剪贴板快照]
  Input --> Process[本地有序预处理]
  Process --> Prepared[冻结产物 / 模板版本 / 后端]
  Prepared --> Policy[统一发送前策略校验]
  Policy --> HTTP[单一 HTTP 客户端]
  HTTP --> Provider[显式模型端点]
  Provider --> Stream[严格响应解析与限额]
  Stream --> Generation[请求 ID 与会话代次校验]
  Generation --> Panel[受保护的文本阅读器]
  Generation --> Clipboard[完整响应 / 原子剪贴板写回]
  Pause[暂停 / 锁屏 / 保护异常] --> Revoke[代次失效 / 取消消费 / 清除引用]
  Revoke --> Generation
```

`Superb.Core` 不调用 Windows、不保存内容、不发出 UI。模型、策略、处理编排、固定路由、同步去重、请求代次与 HTTP 适配器在此层。HTTP 适配器是生产代码中唯一网络入口，模型输入仅成为用户消息的内容。

`Superb.Desktop` 使用单一 WPF Dispatcher 串行处理用户动作、授权与请求事件。所有 `Coordinator` 公共操作及事件订阅均由该事件循环调用。后台线程只负责截图与纯预处理；返回 Dispatcher 后重新检查操作代次和运行状态。后台回调排队后仍必须重新检查请求 ID，避免取消前排队的回调覆盖新状态。

请求在前端经过 Capturing → Processing → AwaitingApproval，发送后由协调器管理 PolicyCheck → Connecting → Waiting → Streaming → Completed / Failed。运行状态 Ready / Paused / Locked / ProtectionUnavailable 独立存在。取消会增加代次、使旧请求失效，并取消传输；已发送内容无法保证撤回。

处理后的快照替代原始输入引用。`PreparedRequest` 保存独立快照、不可变处理配置、无正文的可选步骤状态、后端和参数。手动重试保留这份处理结果并创建新尝试 ID。每次模型调用只有一条用户消息，不复用历史上下文。

## 配置基线

| 参数 | 开发默认值 |
| --- | --- |
| 平台 | Windows 11 x64，启动约束构建 22000+，实测版本待冻结 |
| SDK / GUI | .NET 10 / WPF，不依赖 WebView |
| 协议 | Chat Completions；可添加多个兼容后端 |
| 端点 | 无预置供应商；必须完整显式配置 |
| 请求超时 | 120 秒，可配置 1–600 秒 |
| 输入 | 默认 8 MiB / 后端，绝对上限 16 MiB、图像 3200 万像素 |
| 输出 | 默认 200000 字符、4096 tokens，分别可配置 |
| 重试 | 无自动重试；手动重试独立计费/尝试 |
| 同步 | 默认关闭；10 分钟会话授权，上限 60 分钟；400 ms 去抖、最短 2 秒采集间隔 |
| 输出写回 | 默认关闭；只写完整响应，原子序列比较后写入 |
| 采集超时 | 5 秒停止等待，最多一个未结束原生截图任务 |
| 保护检查 | 创建、渲染、显示、移动、缩放和 100 ms 定时检测 |
| 本地留存 | 仅设置与 DPAPI 凭据；无正文日志/会话文件 |
| 性能承诺 | 尚未建立；没有伪造延迟或资源基准 |

## 需求追踪

“已实现”表示代码路径存在；所有 Windows 行为仍需平台验收。

| 需求 | 代码位置与状态 | 必须补充的验证 |
| --- | --- | --- |
| IN-01/02/05 | `CaptureService`：窗口/显示器/矩形、目标身份与物理范围冻结、仅内存、失效停止 | 多屏、DPI、遮挡、GPU 窗口、黑帧、目标变化；当前非 WGC |
| IN-03 | `ClipboardService.Read`：Unicode / 静态位图、格式/容量/占用错误 | 延迟渲染、多格式、外部占用、超大位图 |
| IN-04/06 | `SyncFilter` / `SyncGrant` / `HotkeyManager`；限时同步、去抖、去重、来源过滤 | 快捷键冲突、持续变化、输出回环、真实事件顺序 |
| PR-01/02/03 | `Preprocessor` / `ImageProcessor`：有序、强制与可选、直接转发、像素遮盖与重新编码、冻结版本 | Windows 像素级测试已提供，未在当前 Linux 会话运行 |
| BE-01/02/03 | `Backend` / `Routing` / `Policy`：多配置、凭据引用、固定路由、能力/限额检查 | 实际供应商的协议兼容性；目前只有一种适配器 |
| BE-04/05/06 | `ChatCompletionsClient` / `Coordinator`：分片 UTF-8、终止信号、代次、取消、超时、手动重试 | 核心与本机 HTTP 测试通过；真实公网和供应商待测 |
| UI-01 | `ReaderWindow`：置顶、滚动、换行、代码块、大小位置字号、显隐持久化 | 原生 WPF 视觉与交互检查未执行 |
| UI-02/03/04/05 | `Protection`：渲染前属性校验，失败清空，状态不宣称已实测；正文只进阅读器 | **发布阻断**：所有采集链路逐帧排除、首次显示/重建/移动/缩放尚未实测 |
| UI-06/07 | NOACTIVATE / TOOLWINDOW / TOPMOST、WM_MOUSEACTIVATE、无自动弹窗/声音/闪烁 | 鼠标悬停激活选项、前台 HWND、Alt+Tab、任务栏缩略图 |
| UI-08 | `EmergencyPause`：立即清空显示、撤销授权与请求、默认清除自有剪贴板 | 锁屏/切换/取消回调与 UI 压力测试 |
| CB-01/02/03/04 | `ClipboardService.Write`：完整响应、来源/代次标记、同锁内比较并写入、历史/云标记 | Windows 剪贴板竞争、历史/云实测、无递归调用/重复扣费 |
| SEC-01/02 | 配置与密钥文件分离；DPAPI 当前用户；无内容日志/遥测/上传 | Windows DPAPI 测试已提供；磁盘/ProcMon 检查和故障路径审计待执行 |
| SEC-03/04/05 | 单一网络入口、证书默认验证、禁止重定向/自动重试；0.1.1 新增显式直连/系统/自定义代理；本地文本渲染 | 参见后端连接排查中的真实 HTTP/代理/TLS 测试；桌面采集链路另行验收 |
| SEC-06 | WTS 锁屏/断开通知、挂起、暂停、退出；恢复不重启同步 | 真实系统会话切换和异常退出验证 |
| 所有 P1 | 未实现，保留后续扩展边界 | 连续截图、OCR、多输入、上下文、队列、授权故障切换、指针穿透、过期清除 |

## 需要保留的技术限制

1. API 属性校验只能发现部分异常，不能证明任意采集路径安全。没有对外标记“当前配置已测试”的按钮；只有实际取得对应构建和软件版本的证据后才能发布此结论。
2. PrintWindow 可能由目标程序绘制，硬件加速窗口可能黑帧、空白或不准确。若选定目标不可靠，应阻止发布该组合；切换到 WGC 需要另立实现和兼容性验证，不可自行回退全桌面。
3. 原生延迟剪贴板渲染、系统调用与图形驱动可能阻塞。截图已放到后台并限制未完成任务数量，但剪贴板读取仍在 STA UI 线程；严苛响应时延目标需进一步评估独立 STA/helper 进程方案。
4. UI 更新按 100 ms 合并，文档以文本段重建。大型回答的滚动位置保持和连续渲染性能尚需优化与基准；不能把此版本描述为已达成性能指标。
5. 当前托管内存实现是减少持有时间，不是可验证安全擦除。应用外的剪贴板读取、系统历史实现差异、已发送后端内容、同用户/高权限进程和物理摄像不在保护保证内。

## 平台依据

开发时复核了微软原始资料：窗口采集属性的适用范围与限制见 [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)；历史/云标记语义见 [Clipboard Formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats)；悬停激活需要处理 `WM_MOUSEACTIVATE` 见 [The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20240919-00/?p=110283)。这些资料是设计依据，不是本实现的验收证据。
