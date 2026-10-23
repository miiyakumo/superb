# 2026-10-22 对抗诊断与内容保护修复报告

本轮发现并修复了 **WPF 弹出窗口没有继承内容采集排除保护** 的问题。完成本机窗口枚举、生产控件回归、带正对照的 GDI 采集实验及下文限定配置的 OBS 录像检查。随后改用人工原生操作 Chrome，完成下表限定的五项补测；代理的原生 URL 识别故障仍未修复。会议接收端与其他采集组合未验收，不能据此声称软件不可检测或全面防录屏。

## 焦点交互优化（2026-10-23，最新源码与构建）

按用户要求停止 Chrome 人工逐按钮测试后，本次直接修复不必要的激活路径，并给保留的焦点切换入口增加提示。以下修改覆盖后文 10 月 22 日源码分析中的旧行为；旧实测仍只适用于当时运行包。

| 操作 | 修改后的行为 |
| --- | --- |
| 舒适阅读、恢复默认、外观面板空白处 | 父级鼠标预处理不再无条件进入编辑。两个预设按钮在阅读模式下保持不激活，仍正常调整布局；若本来已在编辑模式，不擅自切走用户焦点 |
| 未配置模型时点击发送 | 保留草稿，在浮窗提示“尚未连接模型，请点击‘设置’连接；打开设置会切换焦点”；不自动显示或激活设置窗。用户主动点击设置仍正常打开 |
| 编辑、设置、问题输入框、场景下拉框 | 保留正常的焦点切换，以支持输入、输入法与键盘选择；悬停说明明确写出会把焦点切到 Superb |
| 外观滑块、外观滚动条、右下角调整大小 | 保留原拖动进入编辑机制，增加“拖动会切换焦点，完成后按 Esc 返回阅读”提示。没有将保留方案表述成所有实现都无法优化 |
| 阅读/编辑状态说明 | 阅读态标明“输入、场景选择和拖动会切换焦点”；编辑态显示“焦点已切入 Superb”。完成编辑提示为“尝试返回进入编辑前的窗口”，不保证系统焦点恢复成功 |

实现：`AppearancePointerDown` 按真实原始命中元素向父级查找，只让 Slider、ScrollBar 或调整大小手柄进入编辑，按钮标题及其子元素不再触发。保留现有鼠标事件处理，不设置 Handled、不补发点击、不修改穿透、截图选点及内容保护代码。按钮快捷键子标题原本有独立提示，本次将操作提示合并进去，并同步到辅助功能 HelpText，避免鼠标停在快捷键文字上时看不到焦点说明。

验证：Release 解决方案构建 **0 警告/0 错误**，Windows 自动测试 **41/41**，核心测试 **61/61**。新增回归覆盖真实 WPF 模板中按钮文字的预览鼠标事件传播、预设布局生效、不激活样式保持、拖动入口仍发出进入编辑命令、焦点提示可读取，以及无模型发送不显示设置且保留草稿、显式设置入口可用。界面已生成并检查独立渲染图。此次没有进行 Chrome 人工补测或原生鼠标输入验证，不把 WPF 合成事件回归写成浏览器实测。

独立 Windows x64 自带运行时包：[Superb.exe](../artifacts/focus-behavior-20261023/win-x64/Superb.exe)。保留同目录依赖；未覆盖或重启用户原实例，运行中的旧包不会自动得到本次改动。构建包含当前工作区已有功能修改，相关文件未回退。验证与哈希：[verification.json](../artifacts/focus-behavior-20261023/verification.json)。

## Chrome 人工补测结论（2026-10-22 21:32，Asia/Shanghai）

当前实际运行包为 `artifacts/markdown-build/bin/Superb.Desktop/release/Superb.exe`，目标 PID 33980；程序集 SHA-256 为 `2128C08ACCC1580836007C0969B6FB1E727CAAA36429002F7CD15E0D45F79C4F`。这是用户当时已打开的实例；不把这些结果自动套用到其他运行包。人执行真实键鼠操作，代理读取 Chrome 页面事件及最终值，对照人工提示与只读窗口采样。以下均为本机单轮人工功能验收，未进行统计可靠性验证。

| 补测项 | 本轮结果 | 关键证据 |
| --- | --- | --- |
| 两点选择 | 符合预期 | 用户确认开始选点及已加入队列；计数 1→1→2，选点未额外触发底层按钮 |
| 取消后点击 | Esc、右键均符合预期 | 2→2→3、3→3→4；右键取消未打开网页菜单，后续左击各到达一次 |
| 输入法候选窗 | 符合预期 | 用户看到候选窗且显隐浮窗后保留；组合提交/取消及后续输入，最终 DOM 为 测试abc |
| 自动补全 | 合成 datalist 范围符合预期 | 选择 ALPHA-TEST-ONE 后重新输入并取消，最终 DOM 为 ALX；不覆盖地址栏建议或个人资料填充 |
| 快捷键周期 | 本轮指定键符合预期 | Ctrl+Alt+3 两次短按、一次长按，后续 abc 无残留修饰键；Ctrl+L、Ctrl+F、F11 进出后为 abc123 |

本次补测未发现可复现、需要新增产品修复的功能故障。记录工具补充了宿主/窗口逐条落盘和合成自动补全靶场。第一次宿主未持续运行、第一次混合快捷键步骤不清晰的记录均保留为证据不足，没有删除或改写成通过；后续独立对照解决了验收缺口。

普通组合键依然留下 Ctrl/Alt 事件与孤立数字 keyup，软件进程与窗口依然可被系统枚举。功能符合预期不等于无交互痕迹。IME 候选窗和建议弹窗是否可被 OBS/会议捕获、弹窗首帧、独占 F 键模式、翻页键、多屏/DPI、不同输入法和重复长时运行不在这五项结论中。

证据入口：[会话结果与范围](../artifacts/adversarial-20261022/manual-chrome/session.json)、[证据索引](../artifacts/adversarial-20261022/manual-chrome/README.md)。后文按时间保留故障排查和试验过程；早期 BLOCKED 状态指当时的自动控制或尚未进行的人工试验，以本节及最后的逐项记录为最新结果。

## 环境与交付

### 浮窗按钮补测（人工操作已停止，改为按机制审查源码）

继续使用 PID 33980 的原实例，程序集哈希与上述人工补测相同。2026-10-22 新增独立记录目录 `artifacts/adversarial-20261022/manual-chrome/floating-buttons/`，保留开始前网页导出，按按钮分别记录 Chrome 点击、窗口 blur/focus、控件 focusin/focusout 与输入恢复。第一项为阅读模式下点击“外观”展开；以人工输入 a 到 c 的时间区间界定测试，返回聊天和记录控制按钮的事件不计入产品结果。只读窗口采样作为独立辅助证据。

“外观”展开：用户报告“已展开／abc／没有”，页面 DOM 确认为 abc。以 14:07:41 UTC 的 a 松开到 14:07:49 UTC 的 b 按下为按钮操作区间，没有网页 click、pointerdown/up、mousedown/up、blur、focusin/out 或 visibilitychange；仍有鼠标移动及进入/离开事件，不能称为零痕迹。随后返回输入框的普通点击单独记录，整轮 window blur 为 0，三个字母按键完整，无日志覆盖。证据为该目录 `appearance-open.json` 及 `appearance-open-result.json`。窗口采样已在人工操作前结束，与此轮无时间重叠，不作为本项原生动作旁证；展开成功来自用户确认。

“外观”折叠：用户报告“已收起／abcdef／没有”，DOM 确认为 abcdef。14:09:57.133–14:10:13.664 UTC（d 松开至 e 按下）区间内没有网页点击、按下/松开、焦点或可见性变化。整份日志有两次 window blur，但均发生在输入 d 建立基线之前，原因未确定，不能归因于本次折叠；原始记录完整保留，无覆盖。证据为 `appearance-close.json` 及 `appearance-close-result.json`。展开与折叠分别验证同一按钮的两个状态转换，不代表其他按钮已通过。

状态：遵照用户要求停止逐按钮人工操作，网页记录器已暂停。编辑/完成编辑仅准备过实验，未取得人工结果，不算实测通过。上述外观两轮未发现需修复的问题，未修改产品代码；其余改为下述源码机制分析。详细进度见该目录的 `session.json`。

### 按三类机制判断 Chrome 失焦与点击穿透（2026-10-22 修复前源码结论）

更新说明：本节保留审查时的代码行为。2026-10-23 已修复“舒适阅读/恢复默认跟随父级进入编辑”及“发送缺少模型时自动打开设置”两条路径；以文首最新修复节为准。

范围：审查当前工作区的 XAML、父级鼠标事件、按钮命令、窗口激活与两点选择路径。以下是**代码推断**，前提为 Chrome 原先有焦点、Superb 处于就绪的阅读模式且非穿透。当前工作区有其他未提交修改，未证明其全部源码与正在运行的 DLL 完全对应；除前述人工结果外，不新增“已实测通过”声明。若浮窗本来处于编辑模式，已取消不激活样式，普通鼠标点击也可能重新激活浮窗，不能套用阅读模式结论。

#### 一、普通按钮：阅读模式下不主动激活，按钮点击不转发给网页

`ReaderWindow` 使用 `ShowActivated=False`；`Protection` 在阅读模式设置 `WS_EX_NOACTIVATE`，对 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。这使窗口接收鼠标操作而不主动获得前台焦点，**不代表点击穿透**。按钮大多另设 `Focusable=False`；仅检查这一属性不足以判断是否失焦，仍须检查父级预处理和命令分支。

| 按钮/操作 | 阅读模式下的焦点行为 | 点击去向与条件 |
| --- | --- | --- |
| 顶部“外观”展开/收起 | 只改变面板可见性，不调用激活 | 浮窗接收；本机两轮已有网页无点击、无操作区间失焦的实测支持 |
| 读剪贴板、移除单项、清空队列、复制回答/复制部分回答 | 正常路径无激活调用 | 浮窗接收；剪贴板读写不等于对网页触发 copy/paste |
| 停止生成、新任务、恢复 | 正常路径不主动激活 | 浮窗接收；若中断截图流程，还涉及恢复阅读模式；“新任务”会清空工作内容 |
| 重答 | 不主动激活，也没有显式退出当前编辑模式 | 浮窗接收；不同于“直接回答重试”的退出编辑路径 |
| 发送、直接回答重试 | 正常发送路径 `SetMode(false)`，若原先编辑则尝试回到先前前台窗口 | 浮窗接收；发送在未配置模型时例外，见第二类；预处理失败可能在退出编辑前返回 |
| 收起、暂停 | 不主动激活；退出编辑时尝试恢复先前前台窗口 | 触发按钮的正常点击在浮窗处理；收起后该区域后续点击自然落到下方窗口。暂停会清空输入/回答，不只是视觉隐藏 |
| 回到最新 | 仅调整回答滚动位置，无激活调用 | 浮窗接收；此项依据当前 `AnswerView.cs`，不追加运行包实测结论 |

“输入与队列”折叠标题也未绑定进入编辑命令，但框架焦点行为未另行实测。禁用按钮不会仅因禁用就把点击转发到另一个进程的网页。此类结论针对正常命中浮窗控件的区域，不保证窗口外部、完全透明区域或全部框架控件的行为。

#### 二、主动交互：会请求激活，Chrome 可能收到 blur

| 入口 | 代码路径 | 结论 |
| --- | --- | --- |
| 编辑 | `RunCommand("interact")` → `SetMode(true)` → `Activate` + `SetForegroundWindow` | 请求前台焦点；成功时 Chrome 失焦，属预期交互 |
| 点击问题输入框、场景选择器 | `Editor_PreviewMouseLeftButtonDown` → `beginEdit` → `SetMode(true, focusDraft:false)` | 同样主动进入编辑，场景下拉不能归到普通不激活按钮 |
| 舒适阅读、恢复默认、外观面板滑块及面板内其他命中区域 | **父级 `AppearancePanel.PreviewMouseLeftButtonDown`** → `AppearancePointerDown` → `beginEdit` | 即便子按钮 `Focusable=False`、自身 Click 不调用激活，父级也会先进入编辑；与顶部“外观”按钮不同 |
| 右下角调整大小手柄 | 同一个 `AppearancePointerDown` | 拖动前主动进入交互，可能导致 Chrome 失焦 |
| 设置 | `Show` + `WindowState.Normal` + `Activate` | 请求激活设置窗口，Chrome 可能失焦 |
| 未配置模型时点击发送 | `SendWorkspace` 的空后端分支 `Show` + `Activate` | 打开连接设置，属于发送按钮的条件例外 |
| 完成编辑 / 编辑模式下 Esc | `SetMode(false)`，对保存的 `previousForeground` 调用 `SetForegroundWindow` | 尝试恢复原窗口；目标不一定是 Chrome，也未检查该 API 返回值，不保证恢复原网页输入框 |

这里只能判断“请求激活/尝试恢复”，操作系统是否允许、Chrome 是否原本聚焦及是否收到事件需由实际环境决定。网页收到 blur 本身不能识别具体是 Superb 或哪一个按钮。没有把这些预期的编辑行为判为缺陷，也未为减少失焦而改变其交互语义。顶部文字拖动走 `DragMove()`，涉及 WPF 鼠标捕获，未按普通 Click 无激活推断为实测无失焦。

#### 三、改变点击接收方式：穿透与截图选点必须区分

| 操作/状态 | Chrome 可能收到什么 | 依据和边界 |
| --- | --- | --- |
| 点击“穿透”并成功开启 | **后续**覆盖区域的点击交给下方窗口；若下方是网页内容，网页可收到点击 | `SetMode(false,true)` 设置分层窗口的透明命中样式，退出编辑时尝试恢复先前窗口；开启要求编辑/设置/暂停/退出等关键快捷键可用，否则只提示失败。触发“穿透”的正常 Click 已在按钮鼠标松开时处理，不是主动补发网页点击 |
| 已处于穿透模式 | 通常无法直接点击浮窗按钮；点击实际落到下方 | 用已注册编辑快捷键可进入编辑并清除穿透；不能把“点了按钮外观位置”当成按钮命令成功 |
| 两点截图选点期间 | 两个选点的左键 down/up 由选择器消费；右键取消及 Esc 取消也消费相应周期 | `StageScreenshot(true)` 暂时令浮窗穿透，但 `TwoPointSelector` 安装临时钩子截获选点，故与普通穿透不同；没有使用可激活的选区覆盖窗。已有前述人工选点/取消对照支持正常路径 |
| 选点期间其他输入 | 移动、滚轮、中键等未被这个选择器统一拦截，可能到达下方窗口 | 仅特定左/右键与 Esc 路径返回拦截；启动前已按下的鼠标键、取消时序及钩子建立/撤销间隙不能从正常路径推成绝对零事件 |
| 截图成功、右键/Esc 取消后 | 正常结束路径恢复非穿透阅读模式；后续普通点击按窗口命中关系分发 | `finally` 中 `SetMode(false)`，配合 `Show/Hide`；并非恢复进入截图前的编辑/穿透状态。若此前设置窗可见且截图未成功，代码会重新 `Show()` 设置窗，可能另带焦点变化 |
| 截当前屏 | 不进入两点选择钩子；暂时隐藏浮窗再恢复阅读 | 隐藏期间该区域后续点击可能直接落到 Chrome；代码没有把触发截图按钮的 Click 转发给网页，也没有覆盖整个采集期间的输入拦截 |

代码依据：[窗口激活与穿透](../src/Superb.Desktop/Platform/Protection.cs)、[浮窗模式和事件](../src/Superb.Desktop/ReaderWindow.xaml.cs)、[父级事件绑定](../src/Superb.Desktop/ReaderWindow.xaml)、[外观面板预处理](../src/Superb.Desktop/ReaderWindow.Appearance.cs)、[命令分发](../src/Superb.Desktop/MainWindow.xaml.cs)、[发送及截图流程](../src/Superb.Desktop/MainWindow.Workspace.cs)、[选点事件消费](../src/Superb.Desktop/Platform/TwoPointSelector.cs)、[回答滚动按钮](../src/Superb.Desktop/AnswerView.cs)。本次仅修正文档分类，没有新增产品代码改动或运行时验证。源码快照哈希保存在 `manual-chrome/floating-buttons/code-review-sources.json`，便于区分后续源码修改。

日期按 Asia/Shanghai，原始 JSON 使用带时区的 UTC 时间。测试基于当前工作区源码，基线提交 `de2b07e73d69d465f0d589f933e8f857d23bb340` 加本地改动；保留本轮开始时已有的网页、FocusHost 和文档修改。应用版本仍为 `0.2.0`，没有把源码修复伪装成已发布的新版本。

| 项目 | 实测环境 / 文件 |
| --- | --- |
| 系统 | Windows 11，`10.0.26200.0`，用户会话 1 |
| 工具链 | .NET SDK `10.0.402`；Node `v24.21.0` |
| 网页验证 | 应用内浏览器，UA Chromium `155.0.0.0`，DPR 1；独立于 Windows 原生输入链路 |
| 桌面尺寸 | 网页环境报告 2560×1440，工作区 2560×1392；没有覆盖多屏与其他 DPI |
| 本地看板 | [tools/interaction-probe/index.html](../tools/interaction-probe/index.html) |
| 修复后的运行包 | [Superb.exe](../artifacts/adversarial-20261022/win-x64/Superb.exe)，必须保留同目录依赖 |
| 构建身份 | [manifest.json](../artifacts/adversarial-20261022/manifest.json)，含 exe 与程序集 SHA-256 |
| 原始证据 | [本轮证据目录](../artifacts/adversarial-20261022/) |

本轮启动过当前 Release 产品进程并只读审计其窗口。控件、失效、恢复和 GDI 实验使用同一生产 `App`、`ReaderWindow`、`MainWindow`、`Protection` 与 `CaptureService` 的隔离测试宿主；内容为合成数据，不加载用户配置，不读取真实剪贴板，不调用真实模型。GDI 实验仅抓取测试背景覆盖范围内的 32×32 像素，未保存桌面图像。

## 发现与处理

### F-01：可见弹窗未受保护，而所属窗口仍报告保护正常（已修复，优先级高）

修复前在真实 HWND 上读到：

| 操作 | 弹窗 affinity | 所属阅读窗 Check |
| --- | --- | --- |
| 打开场景下拉框 | `0` / WDA_NONE | true |
| 关闭后再次打开 | `0` / WDA_NONE | true |
| 显示 ToolTip | `0` / WDA_NONE | true |
| 显示 ContextMenu | `0` / WDA_NONE | true |

场景名称、模型连接名称以及提示内容可能出现于这些独立窗口。所属主窗属性正常不能覆盖该风险。证据见 [修复前结果](../artifacts/adversarial-20261022/before/popup-results.json) 和 [修复前窗口快照](../artifacts/adversarial-20261022/before/window-audit.json)。这与 WPF 弹出控件创建独立窗口的机制一致；显示亲和性按 HWND 设置。[WPF Popup 文档](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/popup)、[SetWindowDisplayAffinity 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)。

新增 `TransientSurfaceProtection`：在弹出内容接入 HWND 及明确打开时设置并核验 `0x11`，支持场景/设置下拉框、提示框、右键菜单和子菜单；重新打开时重新绑定实际 HWND。应用现有保护检查同时检查这些可见表面；属性丢失时立即折叠内容，所属阅读窗检查失败并清空草稿/回答，生产主窗的现有守卫进入保护失败处理。只有明确重新打开且核验成功时才恢复弹窗内容。隐藏、销毁、重建 HWND 及重复打开均纳入检查。

此修复不改变进程名、窗口枚举可见性、系统监控或浏览器事件机制。保护检查仍存在采样间隔；`Opened` 回调和属性核验不能证明第一帧绝无暴露。输入法候选窗、外部文件对话框、未来新增的任意第三方 Popup 不自动获得本次验收结论。

### F-02：程序存在与窗口样式可被系统直接观察（边界，未做隐藏）

当前产品启动时枚举到目标 PID 的 **11 个顶层窗口，其中 1 个可见**。可见 `Superb` 阅读窗带置顶、工具窗口、不激活、分层样式和 affinity 17；隐藏设置窗也可被枚举。另有 WPF Hidden Window、资源通知、媒体通知和 IME/Cicero 辅助窗口，部分属性为 0。这些隐藏辅助窗当时没有可见内容，不能仅凭 affinity 0 判成内容泄露，也不能仅凭它们存在判成恶意软件。

证据：[真实启动窗口审计](../artifacts/adversarial-20261022/production-startup.json)。电脑控制工具的应用列表未列出受保护浮窗，不能由此推出不存在窗口；只读 EnumWindows 明确找到它。

### F-03：网页零事件与原生动作成功不能互相替代（工具已加强）

子代理在现有实验台上新增本地窗口 JSON 导入、窗口特征展示、目标进程筛选和逐项证据核对。页面把网页事件、本机窗口快照、人工动作声明分开显示。无动作确认、未记录、暂停、日志覆盖、仅合成事件、时间不重叠或零事件时提示证据不足；人工选择“符合预期”不会使这些缺口消失。

网页没有系统窗口枚举权限；本机审计由独立的只读 `WindowAudit` 产生。导入只在页面内存解析，限制 4 MiB、500 个快照、20,000 条窗口观测，文本采用 `textContent` 展示，不执行导入的 HTML。`displayAffinity: null` 保留为未知，不解释成 0。

## 验证结果

| 验证 | 结果 | 能证明的范围 |
| --- | --- | --- |
| Release 构建 / win-x64 自包含发布 | 成功 | 当前源码可编译、运行包已生成 |
| 核心回归 | 61/61 | 核心策略与请求行为；结果来自本轮会话测试输出 |
| Windows 回归（含 `--interactive`） | 38/38 | 生产窗口、焦点保持、穿透命中、布局和已有保护断言 |
| 新增 HWND / 失效 / 恢复检查 | 13 项 × 3 轮通过 | 7 项弹窗属性、3 项丢失保护清除、3 项重新打开恢复 |
| GDI 合成色块对照 | 6 次采样 × 3 轮通过 | 本机 BitBlt/CAPTUREBLT 稳定状态的指定小区域 |
| 网页核心、审计、HTTP 自测 | 29/29 | 解析边界、事件逻辑、静态服务限制 |
| 应用内浏览器实际操作 | 通过 | 文件选择器导入修复前后 JSON、选择快照、记录 a、点击靶标、暂停、导出 |
| Windows 原生浏览器 A–I 全量场景 | 未全部完成 | 自动控制仍 BLOCKED；后来人工完成的五项限定场景见文首，不等于 A–I 全覆盖 |

新增每轮 13 项检查：阅读窗下拉框初次/再次打开、提示框初次/再次打开、右键菜单、子菜单、设置页后端下拉框；下拉/提示/右键菜单的保护移除与清空；这三类关闭后重新打开恢复。三轮完整结果在 [after](../artifacts/adversarial-20261022/after/popup-results.json)、[repeat-2](../artifacts/adversarial-20261022/repeat-2/popup-results.json)、[repeat-3](../artifacts/adversarial-20261022/repeat-3/popup-results.json)。

GDI 对照以绿色背景和洋红色提示内容作标记。每轮关闭保护时 3 次采样均得到 1024/1024 个洋红像素；开启保护后 3 次均得到 1024/1024 个背景绿色像素、0 个洋红像素。实验先等待 WPF 动画/渲染队列稳定；未稳定时正对照失败的调试结果不用于验收。最终比较同时核验实际 affinity 与窗口可见性。采样不是首帧或逐帧录像，未推广到 OBS、WGC、PrintWindow 或会议屏幕共享。

网页实测 `a` 形成 1 个完整按键周期，0 个未配对释放，点击靶标为 1；暂停后输入 `b`，观察事件数保持 105、保留日志保持 148。修复前文件导入显示 5 个快照/50 条窗口观测；最终修复后文件显示 8 个快照/85 条观测，场景下拉框显示 affinity 17。浏览器控制台无 error/warn。证据：[浏览器导出](../artifacts/adversarial-20261022/browser-events.json)、[看板截图](../artifacts/adversarial-20261022/dashboard.png)、[零事件证据不足截图](../artifacts/adversarial-20261022/zero-events-evidence.png)。

Windows 回归日志：[windows-regression.log](../artifacts/adversarial-20261022/windows-regression.log)；网页自测日志：[web-regression.log](../artifacts/adversarial-20261022/web-regression.log)；发布日志：[publish.log](../artifacts/adversarial-20261022/publish.log)。

独立复核补强了 GDI 判定：保护开启后仍要求 PopupRoot、ToolTip 和内容元素保持可见，避免将内容已折叠误判为采集排除成功。补强后的三轮均通过。实际导入还发现同名审计文件重新生成后可能不触发浏览器 change 事件，现已在文件选择前清除旧选择，允许直接重导入同一路径。

## 可重复执行

在仓库根目录执行：

```powershell
dotnet build Superb.slnx -c Release
dotnet run --project tests/Superb.WindowsTests -c Release --no-build -- --interactive
dotnet run --project tests/Superb.WindowsTests -c Release --no-build -- --popup-audit --require-protected --capture-check --output-dir artifacts/popup-audit
node --test tools/interaction-probe/core.test.cjs tools/interaction-probe/audit.test.cjs tools/interaction-probe/server.test.cjs
node tools/interaction-probe/serve.mjs
```

本机浏览器访问 `http://127.0.0.1:8787/`，用“本机窗口证据”文件选择器导入 `window-audit.json`。实验台也可以直接打开本地 `index.html`。退出测试服务用 Ctrl+C；本轮临时服务会在结束时关闭。

针对自行启动的目标 PID 进行只读采样（把 `12345` 换成实际 PID）：

```powershell
dotnet tests/Superb.WindowsTests/bin/Release/net10.0-windows/Superb.WindowsTests.dll --window-audit --target-pid 12345 --samples 40 --interval-ms 250 --output artifacts/window-audit.json
```

默认只观察目标进程的桌面顶层窗口，包含不可见窗口；`--all-windows` 扩展到当前桌面，但其他进程的标题不采集。观察器不安装钩子、不注入输入、不读控件正文/剪贴板/屏幕像素。它不枚举子窗口或 message-only 窗口，有限采样可能遗漏短命窗口。实际属性读取失败记录 null 和错误码。

## 未完成项与下一步

### 原生输入补测核对与暂停（同日）

此前只完成了两点坐标归一化/边界与 `CornerClicks` 完整按下释放的逻辑回归，以及受保护阅读窗前台窗口/编辑/穿透样式检查。这些不等于原生浏览器完整热键周期，也不等于真实鼠标取消后的底层点击验收。两点选择宿主已有代码，但当时没有完成真实输入操作；输入法候选窗和浏览器自动补全同样没有实测。

本轮扩充 `--capture-host --output-dir PATH`，在合成窗口中记录鼠标按下/释放、靶标 Click、右键菜单、Esc、文本组合事件，并提供中文输入框和 WPF 自动补全正对照。输入字段仅保存长度和预设合成文本的匹配布尔值；事件仅来自该测试窗口。WPF 自动补全不能代替浏览器自动补全。构建成功，未将新增测试入口本身记作验收通过。

computer-use 在合成靶标上产生过 1 次左键按下、1 次释放和 1 次按钮 Click，说明工具可以驱动该原生窗口。但同轮还出现外部按键和右键输入，工具随后报告 `user input was detected in this window; call get_window_state before continuing`。该轮不作为干净完整基线；没有开始有效的两点选择/取消测试，更没有完成输入法候选窗验证。

用户当时明确表示正在使用电脑，要求暂缓原生输入测试。已停止输入并关闭该轮隔离宿主（经命令行核验 PID 30596）。随后用户已撤销暂停并将补测范围限定为 Chrome；恢复尝试及新的阻断证据见下一节，不再将用户占用键鼠写作当前原因。

本轮诊断记录位于 `artifacts/adversarial-20261022/native-input/input-events.json`，仅用于说明测量受到干扰，不作为通过证据。两点选择/取消/恢复点击、原生三浏览器周期、输入法候选窗与浏览器自动补全继续标记未完成。

### Chrome 恢复排查与商店安装阻断（同日补充）

用户已允许继续操作，当前补测仅针对原生 Chrome。以下区分原生 Windows 窗口通道与官方浏览器扩展通道；两者失败不能混为一个已确认根因。

| 检查 | 证据与结论 |
| --- | --- |
| 原生窗口通道 | 能列出 Chrome 窗口；在用户打开牛客网后，读取状态仍返回无法可靠确定 Windows 浏览器当前 URL，并停止该轮 Computer Use。换成真实网页未解决该错误；具体 URL 提取失败原因未定位。 |
| 浏览器连接清单 | `cua.getState()` 仅返回应用内浏览器和 MCP Apps，没有 Chrome；请求 Chrome 标签页返回 `Browser is not available: chrome`。 |
| 当前 Chrome | 文件版本 `155.0.8059.85`；Local State 的最近活动配置为 `Default`，主进程没有自定义 user-data-dir / profile-directory 参数。未读取浏览历史或账号内容。 |
| Chrome 扩展 | 当前 Default 的扩展目录和 Secure Preferences 均无本机连接清单所列的两个官方扩展 ID；Chrome ID 为 `hehggadaopoacecdllhhajmbjkdcmajg`。 |
| 原生连接组件 | HKCU 的 `com.openai.codexextension` 注册项、所指清单和清单内宿主文件均存在；允许的来源包含 Chrome 官方扩展。此文件检查不等于端到端握手成功。 |
| 应用日志 | `2026-10-22T09:31:54.636Z` 的扩展同步日志记录 `installedPluginStatus=current`、`isPluginInstalled=true`、`isExtensionInstalled=false`。桌面侧插件已安装与 Chrome 扩展已安装是不同状态。 |
| 商店安装 | 用户尝试后提供 `https://chromewebstore.google.com/detail/hehggadaopoacecdllhhajmbjkdcmajg/error` 及“此商品无法购买或下载”。这是用户报告的商店结果，未伪装成代理已读取 Chrome 页面。 |

确定的缺口是官方 Chrome 扩展未安装、受支持浏览器通道未连接；其安装入口又受到商店不可用提示阻断。现有证据不能确定商店响应由地区、账号、网络出口、发布范围还是临时故障导致，也不能证明扩展全球下架或需要购买。扩展缺失不能直接解释原生 Windows URL 提取器的内部失败。

[官方浏览器扩展文档](https://learn.chatgpt.com/docs/chrome-extension)要求通过桌面应用的 Settings → Computer Use 安装扩展，并在当前浏览器配置中确认连接。[公开问题 #29102](https://github.com/openai/codex/issues/29102)及[#36319](https://github.com/openai/codex/issues/36319)有相同 ID、相同商店提示的第一手复现；它们是用户报告，不构成本机根因或官方修复承诺。个别报告因网络出口变化恢复，其他报告未恢复，不能把单个案例推广为本机诊断。

进一步的只读对照（2026-10-22，Asia/Shanghai）缩小了商店问题的范围：

- 公开网页查询可读取[同一官方扩展详情页](https://chromewebstore.google.com/detail/chatgpt/hehggadaopoacecdllhhajmbjkdcmajg)，显示发布者 OpenAI、版本 `1.26.1015.10801`、更新日期 2026-10-21。这反驳“详情页已对所有访问者消失”的解释，但不证明本机账户可安装。
- 本机未携带浏览器 Cookie 的 HTTP 请求访问正式详情地址，返回 HTTP 200，却重定向到 `/detail/empty-title/hehggadaopoacecdllhhajmbjkdcmajg`，页面标题只有 `Chrome Web Store`，没有 OpenAI 名称或扩展介绍。追加 `hl=en` 结果相同；HTTP 200 不能当作扩展详情可用。
- 使用相同机器、相同请求方法、同样的 `hl=en` 参数访问 Google Translate 官方扩展，保留正确详情地址，标题为 `Google Translate - Chrome Web Store`，包含扩展介绍。由此排除“所有商店详情均不能通过该请求方式返回”的解释。
- Windows 系统代理已启用并配置服务器，未配置 PAC；未修改代理，也未采集或公开出口 IP、账号、Cookie。匿名请求也受影响，说明不能仅以用户的 Google 登录状态解释所有观测。

当前优先假设为网络出口相关的分发条件或该扩展的访问资格判定，仍不是确诊。[Google 发布文档](https://developer.chrome.com/docs/webstore/cws-dashboard-distribution)确认扩展可按国家/地区和指定用户限制分发，但公开资料没有披露本扩展的具体规则。下一项有效对照应保持 Chrome 配置与账号不变，在另一条用户正常可用的网络连接上访问同一正式详情页；若结果不变，再比较账户/浏览器会话条件。尚未完成这组对照，不以“更换节点必定解决”作为结论。

**备用恢复路径预检（同日）：Google 商店更新通道可达。** 根据 [Chrome 官方替代安装文档](https://developer.chrome.com/docs/extensions/how-to/distribute/install-extensions)，Windows 可以登记特定扩展 ID 的 `update_url`，由 Chrome 从商店更新服务获取扩展；用户仍需在 Chrome 中确认启用。这是 Google 支持的通用外部扩展分发方法，未找到 OpenAI 对本次商店错误明确推荐此方法的说明，也尚未验证安装成功。

本机对 `https://clients2.google.com/service/update2/crx` 的只读查询返回目标 ID、`status=ok`、版本 `1.26.1015.10801`。已从响应指定的 Google HTTPS 主机取得 CRX3 文件，仅静态检查，没有运行或安装。包大小 1,204,602 字节；SHA-256 `054b22bb2c74d3a37b2c4c3057c83ffecea0385f23db8ef177e482fc5a01fe37` 与官方响应一致。内部 manifest 为 V3、最低 Chrome 116、版本与响应一致。哈希匹配不等于已完成 Chrome 的签名与安装验收。

准备了[登记文件](../artifacts/adversarial-20261022/chrome-official-alternative/register-official-extension.reg)和[撤销登记文件](../artifacts/adversarial-20261022/chrome-official-alternative/undo-registration.reg)，**均未执行**。登记文件只在 `HKLM\SOFTWARE\WOW6432Node\Google\Chrome\Extensions\hehggadaopoacecdllhhajmbjkdcmajg` 写入 Google 官方 `update_url`。准备时该项不存在；这是机器级登记，需管理员权限，当前执行会话不是管理员。撤销文件仅删除这个登记项，应仅用于撤销本次新建项，不应在该项被其他安装程序修改后盲目使用。

可操作步骤：用户保存浏览器工作并完全退出 Chrome，审阅并导入登记文件，重新启动 Chrome，在扩展管理页核对 ChatGPT 扩展及其 ID、版本，再审阅权限并确认启用。实际 manifest 请求 `<all_urls>`、debugger、history、bookmarks、downloads、nativeMessaging 等权限；启用代表授予相应浏览器能力。若浏览器拒绝、无扩展出现或曾卸载而被外部安装阻止，则保留失败证据，不修改阻止记录或关闭浏览器安全检查。

证据：[Google 更新响应](../artifacts/adversarial-20261022/chrome-official-alternative/google-update-response.xml)、[包检查摘要](../artifacts/adversarial-20261022/chrome-official-alternative/package-check.json)。本地 CRX 仅作为静态验证样本，不将直接拖入或解包加载当作受支持的 Windows 安装路径。

若用户不采用这条安装路径，另一条有效测试路线是用户在原生 Chrome 手动输入与点击，代理运行现有隔离宿主和只读窗口审计、分析网页主动导出的日志。该路线无需浏览器扩展，能保留真实 Windows 输入，但需要用户参与，且不能把人工记录的候选窗现象写成自动采集证明。现有工具尚不具备完成全部五项无人值守原生 Chrome 验收的已验证替代通道。

本轮只做受支持连接尝试与只读诊断，未修改浏览器权限、网址策略、注册表或扩展文件，未重启或清空用户浏览器。没有新增 Superb 产品缺陷的实测证据，因此未为此修改产品代码。诊断摘要见 [chrome-connection.json](../artifacts/adversarial-20261022/chrome-connection.json)。

恢复验收顺序：先确认官方扩展通道能枚举 Chrome 并读取本地合成页面的实际 URL；随后分别验证页面输入与 Windows 全局快捷键正对照。浏览器扩展的页面输入不能自动视作 Windows 原生输入。只有原生输入链路也成功，才继续两点选择、右键 / Esc 取消后点击、真实 IME 组合与候选窗、合成自动补全及快捷键周期。五项当前仍为 **BLOCKED / 未验收**。

### Chrome 扩展安装后的连接验收（同日，覆盖前述当前状态）

用户随后提供已安装的 ChatGPT 扩展信息：版本 `1.26.1015.10801`、ID `hehggadaopoacecdllhhajmbjkdcmajg`。工具清单独立观察到 Chrome 的 `extension` 连接，说明已完成一次连接握手。随后打开页面时 Chrome 暂时不在清单中；只读进程检查和应用清单同时确认 Chrome 已退出，不能据此认定扩展自身不稳定。通过受支持的 `sky.launch_app({app:'Chrome'})` 启动后，同一扩展实例重新上线。

使用重新返回的 Chrome 浏览器 ID 成功新建本地合成测试页，工具返回标题“Superb · 交互痕迹实验台”、实际 URL `http://127.0.0.1:8787/` 及完整页面控件。因此，**官方扩展通道的 URL 识别和页面读取已经恢复**，此前“扩展未安装、Chrome 不在连接清单中”的结论仅适用于安装前。未自动操作扩展权限，也未推定用户采用了哪种安装方法。

本项证明浏览器扩展通道可用；原生 Windows 窗口 URL 校验、真实全局快捷键、两点选择、取消后的底层点击、IME 候选窗和自动补全尚待后续独立验收，不能因连接成功而标为通过。

### 当前可见标签页与原生窗口对齐复查（同日）

上轮原生读取选择的窗口标题仍为 `New tab - Google Chrome`，随后工具因无法可靠确定当前 URL 而终止该轮。该次失败不能证明实验台页面上的原生读取也失败。

本轮直接绑定 Chrome 现有的唯一新标签页并导航到 `http://127.0.0.1:8787/`，没有另建后台测试标签。浏览器工具返回实验台 URL 和控件；随后 `sky.list_windows()` 返回唯一 Chrome 窗口（ID `197838`），标题已变为 `Superb · 交互痕迹实验台 - Google Chrome`。因此本轮已对齐浏览器页面和原生窗口标题，排除了上轮新标签页仍被选中的条件。页面截图见 [chrome-visible-probe.png](../artifacts/adversarial-20261022/chrome-visible-probe.png)。这张图来自浏览器页面截图，不是桌面截图保护的证据。

对已对齐的窗口继续调用 `sky.get_window_state`，工具仍返回：`Computer Use has been stopped for this turn because it could not determine the current browser URL on Windows with enough confidence to enforce policy. Stop your work and send a final message noting why Computer Use ended.` 已按要求停止该轮操作。这次独立复现排除了“原生窗口仍停留在新标签页”作为充分解释；但尚未定位 URL 提取失败的内部原因。五项原生补测仍未完成，没有据此判定 Superb 产品缺陷。

### 原生 URL 阻断的离线排查与恢复路径（同日）

本轮没有再次调用失败的 `get_window_state`，也没有发送 Chrome 原生输入。已把上一轮对齐窗口后的阻断补入本报告及 `chrome-connection.json`。

只读诊断结果：

- 应用自身更新工具返回已安装版本 `26.1016.51333`、构建 `21161`、通道 `prod`；更新检查状态为 `busy`。此结果既不能确认有更新，也不能确认已是最新版本；未轮询或安装。
- 本机 Computer Use 技能缓存版本为 `26.1016.51333`，运行时 `@oai/sky` 包版本为 `0.7.12`。版本字段来自不同组件，不能直接按数字大小判断是否不匹配。
- Chrome 文件/产品版本为 `155.0.8059.85`。当前只有一个 Chrome 主进程，未发现自定义配置目录、远程调试或无头启动参数；所有运行中的 ChatGPT 进程来自同一个可执行路径。这不证明机器上没有其他已安装副本。
- 当日日志在 `2026-10-22T09:31:51.621Z` 记录原生通知配置已存在，下一行记录原生管道启动就绪。检查到的当日日志没有给出本次 URL 提取失败的内部原因；这些启动记录不能证明 URL 识别正常。
- 只读检查可见 JavaScript 包源码未找到该错误的完整字符串；未反编译原生程序，也未修改工具实现、配置或网址校验规则。仍无法区分地址栏/无障碍信息提取失败、上下文状态问题或原生组件缺陷，不能把其中某个假设写成根因。

截至本轮，已证实的故障位置是“原生控制在返回浏览器状态前无法确认当前 URL”。已排除的充分解释包括：扩展仍未连接、页面仍停留在新标签页。**没有完成修复，五项补测仍为 BLOCKED / 未验收。**

[官方浏览器故障排查](https://learn.chatgpt.com/docs/chrome-extension)列出更新桌面应用、重启浏览器、确认配置、尝试新聊天、重启桌面应用及反馈等恢复步骤，但未解释本次原生 URL 报错，也未保证这些步骤能修复它。浏览器重启和扩展连接已在此前完成；下一项有依据但尚未验证的不同尝试是保存工作后完全退出并重新打开 ChatGPT 桌面应用，以重新初始化原生控制组件。该操作会中断当前执行，应由用户完成；不自动结束其他聊天的任务。当前尚未要求或执行再次安装扩展。

恢复后的验收入口：保持 Chrome 实验台为当前可见页面，重新枚举并核对唯一窗口，只进行一次原生状态读取。如果成功，先验证一个实际全局快捷键动作，再继续两点选择、右键/Esc 取消后普通点击、真实拼音组合/候选窗、合成自动补全和快捷键周期；失败则保留新的错误，停止同条件重试。新聊天也是官方连接排查选项，但与原生 URL 校验的关联尚未证实，未自动创建新聊天。

若应用重启/可用更新仍不能恢复，另一条可行路线是用户在 Chrome 手动执行真实键鼠操作，隔离宿主记录选择状态和全局快捷键动作，网页导出自身事件，代理分析两组时间线。该路线需要人工配合，尚未执行；不会用网页合成事件或系统级注入替代原生验收。

已准备可供反馈的[最小复现记录](../artifacts/adversarial-20261022/chrome-native-url-repro.md)，不含浏览历史、账号、Cookie 或完整桌面日志，未向外部提交。

### 桌面应用重启后的受控验证（2026-10-22 20:12 后，Asia/Shanghai）

用户报告已重启。新一组应用日志记录 `2026-10-22T12:12:31.039Z` 启动应用、`12:12:37.396Z` 原生管道启动就绪，管道标识与此前不同，支持本次为真实重启。初始 Chrome 和本地静态服务均已停止；已使用受支持启动 API 重开 Chrome，并恢复仅监听 `127.0.0.1:8787` 的静态服务。

Chrome 官方扩展自动重新连接；将其唯一新标签页导航至实验台后，扩展读取到 URL `http://127.0.0.1:8787/`。原生清单返回唯一 Chrome 窗口（ID `657444`），标题为 `Superb · 交互痕迹实验台 - Google Chrome`。[重启后页面截图](../artifacts/adversarial-20261022/chrome-after-restart.png)仅证明页面恢复，不代表原生输入可用。

在上述条件下只进行了一次原生状态读取，仍返回无法可靠确定 Windows 浏览器当前 URL，并要求立即终止该轮。已停止，未发送原生键鼠输入。此次实测说明完整重启桌面应用没有解决本机问题；五项补测仍未完成，不能继续把重启作为已验证修复方案。

### 原生调用链与待应用更新排查（同日，20:21 后）

本轮不调用原生窗口读取，进行只读源码、策略和更新状态排查，得到以下更具体的证据：

1. `@oai/sky` Windows 客户端的 `get_window_state` 将窗口及截图/文本选项交给传输层；该 JavaScript 实现没有读取地址栏或解析浏览器 URL。当前受支持通道使用桌面应用的原生管道。
2. 桌面应用包内 `computer-use-native-pipe` 处理函数将请求交给 Windows helper transport，并把异常的 `message` 返回客户端。helper transport 对原生组件的失败响应读取 `error` 字符串并转为异常。因此错误经过原生组件响应链返回，上层并没有为本次错误提供地址栏匹配、UIA 节点或 URL 规范化的具体原因。这里是调用链定位，不是已经找到原生组件内部的缺陷行。
3. 当前公开 API 只提供窗口状态/操作入口，没有专用 URL 提取诊断接口。所检查的运行时源码中未发现原生组件的 C#/Swift/C++ 实现或调试符号；未反编译、修改或绕开原生组件。现有证据仍不足以确定实际 URL 提取算法为何失败。
4. HKCU/HKLM 下的 Google Chrome 策略键均不存在；Chrome 主进程没有检出的 accessibility/uia/automation 启动参数，Local State 中没有对应实验项。这不证明 UIA 本身工作正常，但不支持“用户通过这些设置关闭了它”的解释。[Google 官方说明](https://developer.chrome.com/blog/windows-uia-support-update)确认 Chromium 从 Chrome 138 默认启用原生 UIA；这是兼容性背景，不是本机故障证明。没有据此改注册表、禁用 UIA 或降级 Chrome。

**更新状态出现了不同于此前 `busy` 的新证据。** 应用更新工具返回当前版本 `26.1016.51333`、构建 `21161`，状态 `restart_required`。同一次应用启动的日志在 `2026-10-22T12:12:31.039Z` 记录启动，在 `12:12:48.134Z` 才记录更新事件 `action=download_completed`、`result=succeeded`、`currentBuildNumber=21161`、`targetBuildNumber=null`。换算为本地时间，更新下载完成发生在 20:12:31 启动之后约 17 秒。这解释了为何“用户已重启”不等于“已经运行更新后的版本”。目标版本没有返回，不能编造版本号或声称此更新包含 URL 修复。

下一项有新证据支持的恢复操作是通过应用提供的更新/重启入口应用已准备的更新。应由用户完成，因为会中断当前执行；之后先核对实际版本/构建与组件是否变化，再决定是否进行一次原生状态验收。如果版本未变或更新仍待应用，不重复同版本窗口读取。更新是候选修复路径，尚未验证成功。

本轮还把上次“完整重启后仍失败”写回报告和 JSON，并扩充了最小复现记录。具体根因仍未解决，五项原生测试仍未通过验收。

### 补充实测：OBS 与会议工具可操作性（同日）

先前 Firefox 的 URL 策略校验阻断，仅证明该次原生浏览器操作受阻，不能推断 OBS 或会议软件无法操作。后续已通过 computer-use 实际启动、操作本机 OBS 32.0.1 和腾讯会议 3.47.2.405。这是已安装版本，未宣称是官网下载的最新版本。

OBS 保持现有显示器来源与采集设置：界面显示采集方式为 `Windows 10 (1903或更新版本)`，主显示器 2560×1440，输出 1920×1080、60 FPS。录制前临时静音桌面音频与麦克风，结束后恢复。没有启动直播或虚拟摄像机。

新增 `--capture-timeline` 测试入口：使用生产 App 资源和 `TransientSurfaceProtection`，以洋红 ToolTip、绿色背景、白色参照块产生 30 次打开/关闭（20 次新控件、10 次复用）。前后两次正对照将自身测试提示框 affinity 置为 0，背景为青色。32 次窗口可见性/affinity 观测全部通过。该测试只操作自己的合成窗口，不向其他应用注入输入。

实际 OBS 录像逐帧解码共 2,273 帧，其中合成场景 1,659 帧：保护阶段连续 1,271 帧，在指定提示框区域未检出洋红标记；前正对照 127 帧、后正对照 128 帧能检出标记。其余 614 帧属于测试场景外，不计入结论。检测使用明确的像素阈值；不能排除低于阈值的残留、未被 60 FPS 录到的更短瞬间，或其他配置下的行为。

这轮结果是“当前 OBS 配置录下的 30 次合成提示框打开过程中未观察到内容泄漏”，不是所有弹窗和所有采集链的保证。下拉菜单、右键菜单、输入法窗口、其他 OBS 采集方式及会议接收端仍需独立验证。

证据：

- [测试窗口时间线](../artifacts/adversarial-20261022/obs-wgc/timeline.json)
- [逐帧统计](../artifacts/adversarial-20261022/obs-wgc/frame-analysis.json)
- [只含合成画面的无音轨录像](../artifacts/adversarial-20261022/obs-wgc/synthetic-only.mkv)，原视频第 62–1720 帧，裁切左上 600×500 区域、FFV1 无损保存
- [可见正对照](../artifacts/adversarial-20261022/obs-wgc/positive.png)、[保护阶段第 16 次打开附近画面](../artifacts/adversarial-20261022/obs-wgc/protected.png)
- 原始录像：`C:\Users\27061\Videos\2026-10-22 17-42-09.mp4`；SHA-256 `76392038633b06decafbcdc17994ab058ca9458bcd95fa5a2be239dd6fb9de05`。包含测试前后本机桌面，不作为对外分享附件。

腾讯会议实际进入了单人测试会议（摄像头关闭、音频未接入）和共享选择器。尝试在合成窗口覆盖桌面时读取选择器，computer-use 返回的是被覆盖区域，不能据此判断会议预览或接收画面。没有点击“开始共享”、邀请他人或录制会议；没有可控接收端证据，因此会议共享仍标为未验收，不能写成通过，也不能写成工具完全不支持。测试会议已结束并核实返回首页；OBS 已停止录制，两个音轨恢复测试前状态。

复现 OBS 合成场景：先通过 OBS 界面开始本地录制，再运行：

```powershell
dotnet tests/Superb.WindowsTests/bin/Release/net10.0-windows/Superb.WindowsTests.dll --capture-timeline --output-dir artifacts/capture-timeline
```

停止录像后，用 `tools/interaction-probe/analyze-capture.py VIDEO --ffmpeg FFMPEG_EXE --output-dir OUTPUT` 解码。脚本要求 numpy、Pillow；默认检测几何仅适用上文记录的本机布局，需要随显示器/OBS 缩放重新校准。

电脑控制工具在读取 Firefox 状态时明确停止 Windows 浏览器操作，原因是该浏览器的 URL 策略校验尚不支持。没有绕过限制继续注入输入。应用内浏览器仅用于网页功能测试；本轮没有合格的 Windows 原生按键/焦点正对照，因此不发布 A–I 的通过结论。

当前授权的补测范围为 Chrome 的焦点与快捷键周期、两点选择和取消后的底层点击、输入法候选窗与自动补全。Edge/Firefox、其他 OBS 采集方式及其他弹窗首帧、会议接收端、多屏/缩放/锁屏/恢复仍保留为未验收边界，不在本次 Chrome 补测范围内。上文 OBS 单配置合成提示框录像、属性回归和 GDI 小范围实验不能替代这些项目。软件仍可被系统窗口和进程枚举发现，本报告不提供不可检测承诺。

### 应用更新生效后的验收准备（2026-10-22 20:29 后）

用户报告已更新。应用自身更新工具确认运行版本为 `26.1016.61520`、构建 `21421`、状态 `up_to_date`；此前为 `26.1016.51333 / 21161`。Computer Use 技能也更新为 `26.1016.61520`，因此本次具备真实版本变化，允许进行一次不同条件下的受控验证。

恢复本地静态服务及 Chrome 后，官方扩展自动连接，当前唯一标签页 `2065137986` 成功打开 `http://127.0.0.1:8787/`。原生窗口清单返回唯一 Chrome 窗口 `31000852`，标题为 `Superb · 交互痕迹实验台 - Google Chrome`。[更新后页面截图](../artifacts/adversarial-20261022/chrome-after-update.png)仅证明页面连接正常。

更新后的单次原生读取仍返回“could not determine the current browser URL on Windows with enough confidence to enforce policy”，并要求立即结束该轮。没有发送原生键鼠输入。此前因立即结束要求未回填的结果现已补齐：更新没有修复本机故障，五项补测仍未验收。

### 不重启、不重复原生读取的诊断（2026-10-22 20:33 后）

本轮只检查组件配置、已有日志、进程权限元数据及官方说明；未调用原生窗口状态读取，未发送浏览器或桌面输入，也未更改权限、策略、插件或浏览器设置。

新增证据：

- 运行中的桌面应用来自新版 Windows 应用包 `OpenAI.Codex_26.1016.8120.0_x64__2p2nqsd0c76g0`；本次启动日志解析出的 Node 运行时目录已由旧版 `2fbcd108736f9c20` 改为 `b2c4f0c90eadfac4`。新版运行时的 `@oai/sky` 包仍标记 `0.7.12`，不能只凭这个包版本号不变认定更新未生效。
- 12:25:24.964Z 和 12:25:25.758Z，启动日志记录 `unified-computer-use` 的 MCP 启动失败，原因是路径不存在；12:25:25.907Z 随即记录该插件安装成功。当前插件清单引用新版运行时，Node、CUA 入口文件及 Node REPL 文件均存在；之前同一轮扩展页面读取也成功。因此这是已观察到的启动异常，但没有证据证明它导致之后的原生 URL 失败，不能据此要求重复重装。
- 通过只读进程令牌查询，桌面应用主进程和 Chrome 主进程均为 `elevated=False`、完整性 RID `8192`（Medium），且都在会话 1。这排除了这两个主进程之间“Chrome 以管理员运行、桌面应用权限较低”这一具体差异；没有检查原生辅助进程令牌，也不据此宣称所有无障碍权限均正常。证据：[主进程权限对照](../artifacts/adversarial-20261022/chrome-host-token-audit.json)。
- 对新启动的桌面应用日志及只读 SQLite 运行日志进行了时间范围检索；12:25:00Z–12:31:30Z 内数据库共有 394 条记录，没有找到原生 URL 提取原因。桌面日志同期有 `ResizeObserver` 通知布局错误，但没有证据将其与 URL 失败关联。Windows Application 日志的对应时段未检出匹配 Chrome/ChatGPT/computer-use 的 1000/1001/1002 崩溃或挂起事件；这些阴性结果不证明组件无缺陷。
- 系统级 `%ProgramData%\OpenAI\Codex\requirements.toml` 不存在。未据此推断云端或其他来源策略不存在，也未改动任何策略。[官方配置说明](https://learn.chatgpt.com/docs/enterprise/managed-configuration)区分 Browser Use 与原生 Computer Use 的控制路径；扩展能读 URL，不能证明原生路径能得到同一结果。

定位边界：目前确认的是原生状态调用在返回可用窗口状态前，因 URL 置信度不足而中止；现有本地日志没有提供提取器选择、候选地址、拒绝原因或相关异常。无法区分“没有读到地址”“读到了但不接受格式”“窗口与地址绑定不确定”等内部情况，它们仅为诊断分支，不是已确认根因。此前 JavaScript 调用链检查也不能替代原生组件内部证据。

下一步需要能区分上述分支的新证据，而非再次重启：向产品支持提供已更新的最小复现，并请求该调用对应的 URL 提取阶段、失败原因码及窗口绑定诊断。若支持方提供受支持的诊断模式或修复版本，再按其步骤验证。官方还列出新聊天用于清除聊天连接状态，但它对本次原生 URL 故障是否有效未知，本轮未擅自创建新聊天，也不把它包装成修复。手动执行五项测试并由实验台记录可以推进产品验收，但无法单独查明控制工具的 URL 故障。

已更新[最小复现记录](../artifacts/adversarial-20261022/chrome-native-url-repro.md)及机器可读诊断；未向外部提交。仍未找到可在 Superb 仓库内实施的原生通道修复，未为此修改产品代码。

### 人工 Chrome 补测：实验台与双侧记录已就绪（2026-10-22）

采用人工键鼠操作、代理核对记录的路径。隔离 FocusHost 使用生产 ReaderWindow、HotkeyManager、TwoPointSelector；新增可选 `--journal`，以追加 JSONL 记录 UTC 时间、快捷键动作、选点阶段、完成/取消结果、命令状态和每秒简要状态。不会采集桌面像素，不读取用户配置、剪贴板或调用外部模型。主进程状态中的草稿属于测试宿主；周期记录只保存草稿长度。

实验台新增合成 datalist 建议输入框及 IME/自动补全人工核对项目，扩大点击靶标便于在其内部选择两个不同点。datalist 验收不能代表地址栏建议或个人资料自动填充；输入法候选窗需要操作者真实观察。网页仍仅在内存记录，代理将在每个检查点保存导出，操作期间不要刷新页面。

准备验证：Release 构建 0 警告/0 错误；记录器 29 项测试通过；宿主实际启动，快捷键注册失败列表为空，日志持续写入，初始选择状态 idle。Chrome 实验台已加载新增控件并开始记录，E 项已留开始标记。自动化准备点击不计为人工原生验收；尚无五项测试通过结论。

本轮证据位于 `artifacts/adversarial-20261022/manual-chrome/`：`session.json`、`host.jsonl`、`prepared.png`。第一步等待用户完成普通点击基线、Ctrl+Alt+2 两点选择及结束后普通点击，再由代理对照网页计数和宿主完成状态。每个检查点确认后才进入下一项。

### 首轮证据核对及改用当前实际实例（2026-10-22 21:05 后）

用户回报首轮计数“1、1、2”。导出的 `manual-chrome/first-checkpoint.json` 无日志覆盖，含四次可信靶标点击（13:03:53.568Z、13:04:19.261Z、13:04:20.050Z、13:04:20.294Z），最终计数为 4。此快照不能独立还原用户口述的各检查点，也不能把多出的点击直接归为产品缺陷。

隔离宿主日志最后一条在 12:53:50.774Z，早于上述网页操作；记录中没有 capture 动作或 selection 完成，核查时宿主进程已不存在。其终止原因未确定，此前轮次曾被中断。首轮缺乏匹配的原生完成证据，记为证据不足；不能凭用户计数或网页事件为零宣称通过。

发现当前实际运行实例为 `artifacts/markdown-build/bin/Superb.Desktop/release/Superb.exe`（PID 33980）。用户明确要求使用该实例，因此不退出、不替换该程序，也不重新启动隔离宿主。程序集 SHA-256 为 `2128C08ACCC1580836007C0969B6FB1E727CAAA36429002F7CD15E0D45F79C4F`；实际保存配置的 capture 为 Ctrl+Alt+2，普通组合键模式。配置检查仅提取快捷键字段，未读取或输出凭据。

改为三部分证据：网页原生事件、操作者报告的 Superb 状态提示、只读目标进程窗口快照。WindowAudit 增加可选逐条落盘 journal，独立隐藏进程每 2 秒采样、最多 500 条；不注册快捷键、不调用 UIA、不注入输入、不截图。已验证 JSONL 样本持续写入。窗口快照不能证明选择器内部状态或捕捉短暂候选窗；这些边界保留。新版审计工具构建成功，0 警告/0 错误。

首轮导出已保存；网页新轮归零并开始 E 项。下一步使用当前实例重做一次带状态提示确认的两点选择，选区仅含合成靶标。五项测试均尚未形成通过结论。

### 当前实例两点选择：单次人工验收符合预期

用户确认“有／已加入队列／1、1、2”：看到开始选点提示，完成后看到加入队列。网页导出 `manual-chrome/two-point-current-app.json`（13:11:00.933Z）最终计数 2、无日志覆盖。普通基线点击为 13:10:10.033Z，恢复后的普通点击为 13:10:33.243Z；Ctrl/Alt 按下出现在 13:10:12.279Z / 12.365Z。结合用户确认，两点选择没有额外增加底层按钮计数，结束后普通点击生效。本次判为单轮人工行为验收符合预期，不代表所有时序或截图内容已验证。

同时记录到普通 Ctrl+Alt+2 的 Ctrl/Alt 事件及孤立 Digit2 keyup；普通组合键的页面事件残留并非零，保留给快捷键周期测试核对，尚不据此判为修饰键卡住。运行实例及只读窗口采样进程仍存在，样本已覆盖本轮时段；窗口属性不是选择器内部完成日志。

下一项保持点击计数 2，先测选中第一点后 Esc 取消，再测右键取消；要求用户确认取消提示并核对普通点击恢复。其余测试仍待执行。

### Esc 取消后的点击恢复：单次人工验收符合预期

用户回报“已取消／2／3”，确认先选第一点，再以 Esc 取消，随后普通点击恢复。`manual-chrome/escape-cancel.json` 导出时间为 13:13:45.294Z，最终靶标计数 3，无日志覆盖。本阶段仅新增一次靶标点击：13:13:21.333Z mousedown、13:13:21.435Z mouseup/click，可信来源为 true；未观察到 Esc 传入网页。之后另有多行输入框点击，与靶标计数分开处理。

结合操作者的取消提示确认，本轮未见第一点误触底层按钮、取消后丢失或重复普通点击。结论限于此次人工时序；没有产品内部取消日志。只读窗口采样仍持续并覆盖本阶段。接下来从计数 3 开始，测第一点后右键取消及是否打开网页右键菜单；两类取消完成后再推进 IME、自动补全和快捷键周期。

### 右键取消后的点击恢复：单次人工验收符合预期

用户确认“已取消／3／4／没有弹出右键菜单”。`manual-chrome/right-cancel.json` 导出于 13:15:19.995Z，最终计数 4。本阶段新增一次可信左击：13:14:55.079Z mousedown，13:14:55.193Z mouseup/click；未检出本阶段网页 contextmenu 或右键 down/up。结合取消提示的人工确认，未见第一点误触按钮、右键取消唤起网页菜单或后续左击丢失/重复。

本份环形日志已覆盖最早 172 行，但最早保留事件为 13:09:00.324Z，早于该测试开始 13:14:17.783Z；本阶段时间区间仍完整保留。更早两项已有独立导出，未依赖被覆盖部分。已在保存后为 IME 另起一轮日志，保留当前点击计数 4。

两点选择、Esc 取消、右键取消的本轮人工功能核对均符合预期；不扩展为所有时序或采集隐蔽性的保证。接下来在 Chrome 单行输入框真实输入拼音，观察候选窗及显隐浮窗时的焦点行为，然后检查候选提交、Esc 取消组合和后续英文输入。IME 窗口属于其他进程，当前仅目标进程窗口审计无法直接证明其出现或外观，需人工报告。

### Chrome 输入法候选与组合输入：本轮人工功能核对符合预期

用户报告“候选窗出现／显隐后保留／测试abc”。代理通过 Chrome 页面状态独立确认单行输入值为 `测试abc`。`manual-chrome/ime.json` 导出于 13:18:10.816Z，无事件覆盖、无待释放键。真实 compositionstart、compositionupdate、insertCompositionText 及后续 insertText 事件存在；提交后文本长度 2，第二次组合取消后回到长度 2，英文 abc 后为 5。abc 的 keydown 修饰键均为 false。

13:17:21–13:17:46 组合及输入时段未记录到 window blur 或控件 focusout；期间有 Digit3 的释放记录，与用户显隐操作报告并列保留。候选窗外观与保留由用户确认，没有采集候选窗像素，也不推断其截图保护效果。

原始记录中的三个 compositionend 均标记 isTrusted=false，而 compositionstart/update 和输入事件为 true；未修改这一标记，未查明事件来源，也不将这些结束事件独立当作原生证据。结合人工实际输入、最终 DOM 值与其余可信输入事件，本次只给出功能行为符合预期的结论。全轮 21 次未配对 keyup 不能直接等同于按键卡住；IME 处理及组合键事件分流仍需按类型解释。

下一项为 Chrome 合成 datalist 下拉建议的选择、显隐浮窗时的表现、Esc 关闭和后续输入；单独开启日志，原有 IME 证据已保存。个人资料自动填充及地址栏建议不包括在这项 datalist 结论内。

### Chrome 合成自动补全：本轮人工功能核对符合预期

用户回报“建议出现／显隐后保留／ALPHA-TEST-ONE／ALX”；代理独立读取到最终 DOM 值 `ALX`。`manual-chrome/autocomplete.json` 导出于 13:20:57.917Z，无日志覆盖、无待释放键。13:20:32.743Z 有可信 input 将值长度置为 14，与人工选择的 ALPHA-TEST-ONE 长度一致；重新输入 AL 后长度为 2，13:20:40.886Z 输入 X 后长度为 3。X 事件的 Ctrl/Alt 为 false、Shift 为 true，符合使用 Shift 输入大写，不将其误报为残留。

建议展开、显隐时保留和 Esc 关闭由人工确认；网页没有收到本轮下拉选择用的完整方向键/Enter/Esc 序列，不据此推断这些原生 UI 动作没有发生。本轮仅覆盖合成 HTML datalist，不代表地址栏建议、密码或个人资料自动填充。

四类项目已有本轮功能核对结果（两点选择、两种取消恢复、IME、datalist）。已保存后为最后一项开启独立日志：普通 Ctrl+Alt+3 两次短按与一次长按，随后 Ctrl+L、Ctrl+F、F11 进入/退出及后续英文输入。该计划不覆盖独占 F 键模式或没有可翻页内容的翻页快捷键；这些边界会留在最终报告中。

### 快捷键长流程首次核对：证据不足，拆分短对照

用户概括报告“正常”，但页面实际单行框为 `ade`，多次后续字母输入发生在自动补全框。`manual-chrome/shortcut-cycles.json`（13:25:17.249Z 导出）无日志覆盖，无待释放键，4 次未配对释放；记录到 Ctrl+L、Ctrl+F、F11 相关事件及普通输入，但不足以确认原计划每一步的焦点与显隐次数。

13:24:25.144Z 的 b 按下时 Ctrl/Alt 都为 true；随后 13:24:33.414Z / 33.589Z 才分别记录 Alt / Control 释放，之后 b–g 的修饰键均为 false。操作者表示记不清此前输入 b 时是否仍按着修饰键，不能将其直接判为软件残留，也不能宣布完整快捷键周期已通过。

原窗口审计按设定完成 500 次采样并正常输出 `current-app-windows.json`，不是宿主异常终止。为接下来的短对照另启独立、只读采样到 `shortcut-windows.jsonl`。保留前四项结果，仅重做同一单行框中的两次短按显隐与一次长按显隐，每次全部松开并停一秒再输入字母。Chrome 地址栏/查找栏/全屏的确认随后单独进行，以免把混合操作误归因。

### 普通显隐键短对照：本轮人工验收符合预期

用户明确回报“两次短按正常／长按只显隐一次／abc”。代理独立读到单行框 `abc`。`manual-chrome/toggle-cycles.json`（13:28:28.279Z）无日志覆盖、无待释放键；a、b、c 的可信按下/释放成对，Ctrl/Alt/Shift/Meta 均为 false。普通 Ctrl+Alt+3 留下 3 次孤立 Digit3 keyup，保留为普通组合键传播事实，不等同于残留卡键。

目标进程窗口审计在 13:27:40.316Z 看到可见窗口 0x17B01D8；13:27:48.389Z 隐藏，13:27:54.435Z 再次可见，13:28:02.511Z 再次隐藏，与两次短按、一次长按的用户观察相符。1 秒采样不能排除更短瞬间，故长按只切换一次仍以人工观察为主并与采样并列。

本次受控释放后输入没有复现前一轮的修饰状态歧义，未发现需要改动产品的卡键或重复显隐问题。最后只剩单独核对 Chrome Ctrl+L、Ctrl+F 和 F11 进入/退出；保持单行框 abc，在三个步骤后分别追加 1、2、3。

### Chrome 自身快捷键最终确认与收尾

用户明确报告“正常／正常／正常／abc123”。代理独立读取到单行框最终为 `abc123`。`manual-chrome/chrome-shortcuts.json` 于 13:30:59.046Z 导出，记录已暂停，无日志覆盖、无待释放键。1、2、3 的可信按下/释放均配对，Ctrl/Alt/Shift/Meta 为 false；视口从 1614×899 变为 2560×1440，再恢复 1614×899，与人工确认 F11 进入/退出一致。

Ctrl+L 地址栏选中、Ctrl+F 查找栏打开/关闭由操作者明确确认，网页保留相应快捷键/失焦/输入线索。用户按步骤点击回单行框后输入，因此只确认“手动回到网页后输入正常”，不宣称 Esc 必然自动恢复到原输入框。浏览器原生控件处理部分按键，孤立 keyup 不单独当作产品缺陷。

五项约定补测至此完成本轮限定的人工功能核对。没有发现需新增产品修复的可复现故障；此前弹窗保护修复仍是独立结果。网页记录已暂停并导出，首个只读审计进程按计划完成 500 条，第二个确认身份后主动停止，284 条已落盘样本全部解析成功并汇总为 `shortcut-windows.json`。用户现有 Superb 保持运行，本地静态实验台仍可查看；隔离 FocusHost 没有重新启动。原生 URL 校验工具故障仍未修复。
