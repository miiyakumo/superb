# Superb 本机交互痕迹实验台

独立的纯静态中文诊断页，针对 Superb 浮窗、全局快捷键、两点截图、穿透和剪贴板交互收集**网页可观察的证据**，并可手动导入独立工具生成的本机窗口审计 JSON。两种来源分栏展示，只按时间并列，不自动建立因果关系或判定通过。不修改 Superb 配置，不安装或启动全局钩子，不调用模型。旧版 `tools/event-probe` 继续保留；本页补充捕获／冒泡、按键周期、剪贴板元信息、时间标记、逐项核对与高级浏览器事件。

## 打开

可以直接双击本目录 `index.html`。推荐在仓库根目录执行（Node.js 18+，无依赖）：

```powershell
node tools/interaction-probe/serve.mjs
```

访问 `http://127.0.0.1:8787/`。可以传入端口，例如 `node tools/interaction-probe/serve.mjs 8890`，也支持 `PORT` 环境变量。服务只绑定 `127.0.0.1`，仅 GET／HEAD 提供 HTML、CSS 和三份 JS；拒绝路径穿越、请求体、其他文件和写入方法。不接收、不打印、不保存浏览器日志。关闭终端服务用 Ctrl+C。

## 使用与证据

1. 保持 Superb 关闭，打开网页，点“开始记录”。只用页面附带合成文本，先完成 A 基线：普通字符、长按、对照按钮和滚动。
2. 启动 Superb，每项先点“开始本项”留时间戳，再按网页 A–I 清单操作。每次同时检查浮窗动作确实发生，例如显隐、翻页、进入截图选择、设置打开；不要用网页零事件代替原生动作证据。
3. 在每项的备注中记下原生动作和浏览器现象，确认实际看到原生动作后勾选对应复核框，再人工选择“符合预期／观察到异常／证据不足”。选择结果记录结束时间；选择结果只是人工声明，不是自动通过。重新点“开始本项”会清除该项上次人工结果、备注和确认框，以免把上一轮证据算入新一轮。
4. 用过滤器查看事件类别、捕获／冒泡和可信性；搜索支持按键、目标、事件、备注。页面最多显示最近 200 条匹配记录，内存保留最近 5,000 条；高频指针移动／滚轮／滚动／选区等按 50–150ms 节流。统计“观察事件”对同一 Event 对象的捕获／冒泡只计一次；日志及分阶段计数仍分别保留。
5. “暂停”停止采集原始浏览器事件，终止当前按键周期。显式添加标记、选择案例结果、还原靶场、清空和导出仍可产生 `whilePaused` 标记，点击靶标计数也继续显示；这些是用户主动控制操作。继续时重新跟踪按键。
6. “清空”清除日志、计数、备注、案例结果、导入的窗口审计、JSON 预览与待释放按键，重新计时；点击靶标累计次数保留，点“还原测试文本”可以归零。清空后留下单条新轮次标记；若仍在记录，该次点击的后续冒泡也可能进入新轮次。
7. “查看 JSON”只更新页内只读快照；“导出 JSON”手动下载 `superb-interaction-probe-时间.json`。下载是唯一持久化出口，不会自动上传。刷新或关闭页面即丢失未导出的内存记录。

导出格式 `superb-interaction-probe/v1` 包含环境、当前焦点／可见性／全屏状态、摘要、A–I 测试计划及人工结果、最多 100 个标记、有界事件，以及新增的 `importedWindowAudit` 与 `evidenceReview`。窗口审计只保留经过结构校验的支持字段，未支持字段不会随导出保留；原始审计文件应另存。可在浏览器开发者工具读取：

```js
window.interactionProbe.snapshot()
```

这个只读接口返回深拷贝，修改返回值不会改动记录。合成 `dispatchEvent` 事件仍记录 `isTrusted: false`，不会混入可信按键周期；合成结果不能验证 Windows 真实焦点或钩子行为。

## 本机窗口审计导入

在“本机窗口审计”栏选择本地 JSON。文件内容只经 `File.text()`、`JSON.parse()` 和结构校验处理；显示使用 `textContent`，不会执行导入文本中的 HTML 或脚本，不会发起上传或其他网络请求。无效文件保留上次成功导入的数据并显示错误。移除导入／清空／刷新会释放本页中的数据；用户显式导出包含已导入的支持字段。

可用本目录 `sample-window-audit.json` 测试导入。这个文件的窗口、句柄、PID 和属性全部虚构，`source.synthetic: true` 必须显示为合成宿主／示例，不能充当生产运行证据。静态服务不提供示例 JSON 下载；通过本机文件选择器选择它。

接受的格式为：

```json
{
  "schema": "superb-window-audit/v1",
  "capturedAt": "2026-10-22T10:00:01.000Z",
  "startedAt": "2026-10-22T10:00:00.000Z",
  "endedAt": "2026-10-22T10:00:01.000Z",
  "source": {
    "tool": "collector name", "version": "1", "scope": "target PID",
    "synthetic": true, "limitations": ["collector limitations"]
  },
  "snapshots": [{
    "timestamp": "2026-10-22T10:00:00.000Z", "foregroundHwnd": "0x1001",
    "windows": [{
      "hwnd": "0x1001", "processId": 1234, "processName": "SyntheticHost",
      "isTarget": true, "title": "SYNTHETIC", "className": "SyntheticWindow",
      "isVisible": true, "isTopmost": true, "isToolWindow": true,
      "isNoActivate": true, "isTransparent": false, "isLayered": true,
      "isCloaked": false, "ownerHwnd": "0x0", "displayAffinity": null,
      "affinityError": 5, "style": "0x80000000", "exStyle": 136,
      "rect": { "left": 0, "top": 0, "right": 100, "bottom": 50 }
    }]
  }],
  "notes": ["synthetic data"]
}
```

- 必须显式提供正确 `schema`、`capturedAt`、`source.tool`、`source.synthetic`、`snapshots`、快照 `timestamp`／`windows`、窗口 `hwnd`。时间必须含 `Z` 或时区偏移；内部规范为 UTC。省略 `startedAt`／`endedAt` 时取首末快照或采集时间。快照必须落在声明时间范围内。
- 其余窗口布尔和数字字段可省略或为 `null`，表示未知；句柄、标题、类名、来源字段均为字符串，最多 512 字符。`style`／`exStyle` 也接受安全整数。`affinityError` 为整数错误码或 `null`。`source.limitations` 和 `notes` 分别最多 100 条字符串。
- 总文件最多 4 MiB、500 个快照、20,000 条窗口观测。单个快照最多渲染 500 个匹配窗口，其余支持字段仍保留供导出。默认过滤 `isTarget: false`，没有目标归属字段的窗口仍显示并标“归属未知”，避免无依据隐藏记录。
- `displayAffinity: null` 明确显示“未知／不可读”，不等同于 `0`（`WDA_NONE`）。`1` 与 `17` 只代表采样到的设置值，不能验证实际截图排除效果。其他进程标题可能刻意不采集，空标题不是异常结论。
- 可复核特征包括前台、置顶、工具窗口、不激活、透明扩展样式、分层、DWM cloaked、不可见与非零显示亲和性。这些都是普通窗口属性，不能据此认定恶意或证明不可检测。未采到窗口也不能证明不存在窗口。

“逐项证据核对”对每项开始至人工结果时间并列浏览器事件和本机快照，不使用模糊时间容差。浏览器计数仅含靶场／页面目标，不含控制按钮与标记，捕获与冒泡仍按日志行计数。开始时未记录、区间曾暂停、没有结束时间、没有原生动作确认或备注、零网页记录、只有合成网页事件、日志发生覆盖、缺少本机快照和合成宿主都会提示证据不足。即使条件齐备，也只显示“仍需人工复核”，不会自动通过。只应关联同机时钟准确的记录；采样间隔会漏掉短命窗口，时间接近不能证明因果。

窗口审计表和证据核对表自身的滚动不记入浏览器日志，避免看日志产生反馈噪声。`audit.test.cjs` 使用纯合成数据验证格式／大小／数量边界、带时区时间、惰性 HTML 文本、原型污染字段剔除、未知保护状态、零事件不通过、人工声明与完整性分离、暂停区间和时间排除。它不替代真实桌面测试。

2026-10-22 本次增量验证：29 项核心／审计／HTTP 测试通过，JS 语法检查通过；已成功解析真实启动审计（1 快照／11 条窗口观测）。应用内浏览器实际通过文件选择器导入修复前审计（5 快照／50 条观测）与修复后审计（8 快照／85 条观测），切换快照并核对 affinity 17。输入 a 得到一个完整周期，靶标计数为 1；暂停后输入 b，观察事件保持 105、保留日志保持 148。人工结果选择“符合预期”且没有有效证据时仍显示证据不足。详细证据与原生测试边界见 [本轮报告](../../docs/adversarial-report-20261022.md)，截图在 `artifacts/adversarial-20261022/`。

## 记录什么

- 捕获与冒泡的 keydown／keyup：key、code、repeat、location、修饰键、isTrusted、目标、defaultPrevented。
- 按键配对时长、重复次数、未配对释放、超过 3 秒仍未释放与失焦中断提示；同时跟踪最多 64 个键，异常合成键超限时淘汰最早周期，key／code 最多 80 字符。它们均是排查线索，不能单独断言漏键或归因 Superb。
- pointer／mouse 的按下、抬起、移动、进入、离开、点击、双击、上下文菜单和滚轮。
- window focus／blur、控件 focusin／out、visibilitychange、pageshow／pagehide、online／offline。
- selectionchange 只记选区长度；beforeinput／input／composition 只记输入类型、组合状态和长度，不存 data 或控件 value。
- copy／cut／paste 只记 MIME 类型、文件数量、选择长度，以及主动粘贴事件提供的纯文本长度；不保留正文、不调用异步剪贴板 API、不轮询剪贴板。网页复制事件未必提供最终剪贴板类型／内容长度，未知值保持 null／空列表。
- 页面与靶场的 resize／scroll、visualViewport（浏览器支持时）、Fullscreen API／pointer lock 状态及错误、拖放类型和文件数量。拖放测试区阻止文件默认打开，不读取文件名或内容。

不会记录日志查看器、标记列表、JSON 预览区域**自身的滚动**，避免程序化跟随最新形成日志回馈；其他页面和靶场滚动照常。没有全局 preventDefault 或按键屏蔽，只有拖放靶场明确阻止默认打开文件。isTrusted 只说明浏览器对事件来源的标记，不能证明操作者身份。捕获阶段 defaultPrevented 也可能尚未反映后续处理。

## 隐私与能力边界

无外部依赖、请求、遥测、Cookie、localStorage、sessionStorage 或 IndexedDB。CSP 明确 `connect-src 'none'`；服务没有日志上传接口。环境信息包括 UA、平台、时区、分辨率等；file URL 不保留本地完整路径。

**键盘 key／code 可以还原输入。** 输入框正文、粘贴正文虽不直接存储，逐键日志仍可能泄露密码、密钥、真实题目或个人内容；请只在合成文本和专用测试会话中使用，导出前自行检查备注。无需、也不会申请桌面、摄像头、麦克风或剪贴板读取权限。

网页无法直接检测其他进程、全局键盘钩子本体、桌面截图、受保护置顶浮窗或后台剪贴板读取。blur 与 visibilitychange 分开观察；F11 浏览器全屏不等同 Fullscreen API，可能没有 fullscreenchange。浏览器／系统保留键和页面本身处理也会改变事件。**事件为零、孤立 keyup 或失焦不能单独证明 Superb 生效、被检测或不可检测。** 截图排除保护须单独检查采集结果，不在本页自动判定范围。

## 本轮验证

```powershell
node --check tools/interaction-probe/core.js
node --check tools/interaction-probe/audit.js
node --check tools/interaction-probe/probe.js
node --check tools/interaction-probe/serve.mjs
node --test tools/interaction-probe/core.test.cjs tools/interaction-probe/audit.test.cjs tools/interaction-probe/server.test.cjs
```

核心测试覆盖环形容量／覆盖顺序／深拷贝、计数去重、暂停门控、清空、按键周期、可信／合成隔离、异常键长度上限、剪贴板仅元信息、无网络和持久化源代码检查。HTTP 测试检查静态白名单、GET／HEAD、404、路径穿越、拒绝写入与请求体。测试仅使用合成对象和本机静态请求。

这些验证不等于页面完整视觉验收或 Superb 原生对抗测试。后续已在应用内浏览器验证输入 a 的完整按键周期、点击靶标计数和暂停后不继续采集。对照测试发现，浏览器工具发送 Ctrl+Alt+3 时网页收到按键，已注册该键的隔离原生宿主没有收到显隐动作；这条自动输入链路不能用来验收 Windows 全局钩子。原生 A–I 仍保持待测，须实际执行后再给结论；记录见 `../../docs/interaction-adversarial-testing.md`。
