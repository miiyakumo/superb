# Superb

面向 Windows 11 x64 的受控 Agent 转发客户端。使用原生 WPF 阅读面板、Win32 采集与剪贴板接口，以及可配置的 Chat Completions 后端。

**当前为 0.1 开发版本，尚未完成 Windows 实机验收，不能作为已验证的保密产品发布。** 已实现采集 → 本地处理 → 独立发送 → 流式阅读 → 可选剪贴板输出的代码闭环。采集排除、焦点行为、多屏 DPI、锁屏和系统剪贴板兼容性需要实测；没有任何采集软件组合被标记为已通过。

## 运行与构建

开发环境需要 .NET 10 SDK。Windows 用户可以运行：

```powershell
dotnet run --project src/Superb.Desktop
```

生成自带运行时的 Windows x64 文件夹：

```powershell
dotnet publish src/Superb.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

运行生成目录中的 `Superb.exe`，保留同目录依赖。无需管理员权限。首发开发基线为 Windows 11 x64（构建 22000+）；这只是启动约束，正式支持范围以验收矩阵为准。没有配置供应商、API 密钥或有效后端时不会发送网络请求。仅允许一个本机会话实例。

## 首次使用

1. 在“模型后端”新建后端，填写完整端点、模型 ID 和密钥。端点例：`https://your-authorized-host.example/v1/chat/completions`。本地开发服务器只能使用显式授权的 IP 回环 HTTP，例如 `http://127.0.0.1:8080/v1/chat/completions`。
2. 在“采集与策略”选择窗口、显示器，或显示器内的物理像素矩形。窗口目标重启后需要重新选择；移动或调整目标大小后也需要重新确认。
3. 按需配置预处理，默认直接转发。点击“恢复会话”，打开阅读面板。
4. 读取剪贴板或截屏，在阅读面板检查处理后的输入，再点击“发送”。默认不会在采集后自动发送，也不会自动显示隐藏的阅读面板。
5. 完整回答可手动复制。自动写回需要独立启用；剪贴板在请求期间变化时跳过自动写回。

同步需在每次会话中明确授权输入格式、接收后端、期限及是否允许自动发送。期限默认 10 分钟，上限 60 分钟。400 ms 去抖、至少 2 秒采集间隔、内容哈希去重、过滤本应用输出；繁忙时合并为最新一次剪贴板变更。启动授权时不自动读取已有剪贴板。停止同步会取消在途任务并遮蔽结果；界面中的同步表单选项只有点击“开始限时同步”后生效。

默认全局快捷键：

| 快捷键 | 动作 |
| --- | --- |
| Ctrl+Alt+1 / 2 | 读取剪贴板 / 预设截屏 |
| Ctrl+Alt+3 / 4 | 显示或收起阅读面板 / 复制完整回答 |
| Ctrl+Alt+5 / 6 | 取消当前任务 / 紧急暂停 |
| Ctrl+Alt+7 | 发送已准备输入 |
| Ctrl+Alt+8 / 9 | 阅读面板向下 / 向上翻页 |
| Ctrl+Alt+0 / Q | 打开控制中心 / 退出 |

快捷键冲突会列出注册失败的动作。阅读面板不激活、不进入任务栏；设置窗口只在启动和用户明确打开时激活。可以拖动阅读面板标题栏、从右下角调整大小；位置、大小、字号和显隐状态会保存。

## 预处理与固定路由

处理规则按数组顺序执行，裁剪、遮盖的坐标相对上一步图像。以下示例仅用于图像：

```json
[
  { "Kind": "Crop", "Required": true, "Rectangle": { "X": 0, "Y": 0, "Width": 800, "Height": 600 } },
  { "Kind": "Mask", "Required": true, "Rectangle": { "X": 20, "Y": 20, "Width": 180, "Height": 40 } },
  { "Kind": "Resize", "Required": true, "MaxDimension": 1600 },
  { "Kind": "Template", "Required": true, "Template": "请解释图片内容。{input}" }
]
```

文本可使用 `NormalizeText` 和 `Template`。不处理正文时使用空数组 `[]`。仅 `Required: false` 且 `SkipOnFailure: true` 允许失败后跳过；界面显示跳过计数。强制步骤失败不发送。新图像逐像素重新编码，遮盖区域为实色像素，不附加原图或元数据。

固定路由只依据处理产物是否带图像，使用配置中列出的后端 ID：

```json
{ "TextBackendId": null, "ImageBackendId": null }
```

`null` 使用手动选择。自动同步固定绑定获授权的一个后端；路由指向另一个后端时拒绝发送。用户文本、截图内文字和模型回答不会成为策略指令。

## 存储和边界

- 配置保存在 `%LOCALAPPDATA%\Superb\settings.json`，凭据单独保存在 `credentials\*.dpapi`，采用当前用户 DPAPI。配置导出去掉凭据引用及采集目标。规则模板属于持久配置，不要把敏感正文写进模板。
- 截图、剪贴板正文、回答和请求历史默认只在内存；无应用内容日志、自动遥测、崩溃上传或临时图像文件。暂停/锁屏/退出清除显示和会话引用，默认只清除仍由本应用拥有的剪贴板输出。托管字符串、图形缓冲、系统分页与第三方转储无法保证绝对擦除。
- 所有后端流量经过同一个客户端，验证 HTTPS 证书，禁用重定向、Cookie、系统代理和自动重试。若需代理，应把获授权代理自身配置成端点。模型协议目前仅实现 Chat Completions；不支持工具调用、Responses、Anthropic 原生协议、OCR 或浏览器自动化。
- 每次请求独立，流必须包含合法 `finish_reason: stop` 及 `[DONE]` 才完成。截断、长度上限、限流及内容过滤结果不会自动写回。手动重试沿用冻结后的输入和后端，但创建新的尝试 ID，可能产生新的计费。
- 阅读器只生成本地文本与代码段，不解析 HTML/XAML、不自动加载链接或执行命令。系统剪贴板历史/云标记不阻止其他进程读取当前剪贴板。
- 当前采集原型采用 GDI `BitBlt` 和窗口 `PrintWindow`，不是 WGC。拒绝黑帧和变化的几何范围，不做全桌面回退。部分 GPU 窗口可能无法正确采集；需要用验收证据冻结支持范围。PrintWindow 超过 5 秒后停止消费结果，底层调用不能强制终止，最多保留一个在途采集线程。
- `WDA_EXCLUDEFROMCAPTURE` 设置成功只表示属性可检查，不能证明所有采集链路安全。运行中每 100 ms 及敏感渲染前检查；外部改变保护与检测之间存在时间窗口，必须逐帧验证。不存在进程隐藏、注入、关闭系统安全提示或对高权限攻击者的保证。

## 验证

跨平台核心测试（自包含控制台测试器，失败时非零退出）：

```powershell
dotnet build Superb.slnx -c Release
dotnet run --project tests/Superb.Tests -c Release
```

Windows 专用测试：

```powershell
dotnet run --project tests/Superb.WindowsTests -c Release
dotnet run --project tests/Superb.WindowsTests -c Release -- --interactive
# 仅在可覆盖当前剪贴板的测试会话运行：
dotnet run --project tests/Superb.WindowsTests -c Release -- --clipboard
```

普通 Windows 测试只处理合成位图和一次性合成凭据；`--interactive` 检查窗口属性，不能替代采集排除实测；`--clipboard` 会覆盖当前剪贴板。CI 已配置 Windows 构建、核心/图像/DPAPI 测试及打包，尚未运行远端工作流。

详细设计、需求覆盖和发布门槛见 [架构与需求映射](docs/architecture.md)、[Windows 验收矩阵](docs/acceptance.md)、[本次验证记录](docs/verification.md)。原始需求保存在 [requirements.zh-CN.txt](docs/requirements.zh-CN.txt)。
