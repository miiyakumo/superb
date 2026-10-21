Superb 本地网页事件测试台

页面用途
  分别记录 window.blur/focus、document.visibilitychange、focusin/out、keydown/up、pointerdown 和 wheel。
  不阻止默认事件，不覆盖焦点/可见性 API，不获取剪贴板，不读输入框正文，不联网。
  按键 code 和修饰键会进入日志，所以只输入合成测试内容。日志在内存中，手动导出后才成为文件。
  每轮最多 5000 条，超出明确计为 dropped；页面只展示最近 100 条，导出包含本轮完整记录。

本机手动复测（Windows 11 + 已安装 Superb）
  1. 在仓库根目录运行：
     python -m http.server 8765 --bind 127.0.0.1 --directory tools/event-probe
  2. 在 Edge 或 Chrome 打开 http://127.0.0.1:8765/，保持网页前台。
  3. 在 Superb 中准备一段足够长的合成回答；配置、模型调用应在测量开始前完成。
  4. 选择场景，点击“开始记录”，在 10 秒内只执行一次相应动作。
     等待自动停止后再返回网页。改变页面控件可能产生 focusout，不要误读为 window.blur。
  5. 显示/收起：Ctrl+Alt+3；向下/上翻页：Ctrl+Alt+8 / 9；点击正文；滚轮；点击设置。
     热键以你的实际配置为准。每种操作重复三次。
  6. 必须做正对照：输入 A 能记录按键；点击普通窗口能记录失焦；切换标签能记录隐藏。
     如果正对照没有记录，不能把其他操作的零事件当作通过。
  7. 导出 JSON 和 HTML。手工报告不会自动证明浮窗操作确实执行，需记录肉眼观察。
  8. 结束服务器：Ctrl+C。不会监听局域网。

可重复的 Windows 自动测试
  dotnet build Superb.slnx -c Release
  python -m venv .venv-focus
  .venv-focus\Scripts\python -m pip install playwright==1.64.0
  .venv-focus\Scripts\python tools/event-probe/run_probe.py --browser msedge --repeats 3 --output artifacts/event-probe/msedge
  .venv-focus\Scripts\python tools/event-probe/run_probe.py --browser chrome --repeats 3 --output artifacts/event-probe/chrome

  注意：自动测试会把测试浏览器放到前台，并在当前桌面合成鼠标/按键；请在独立测试会话运行，不要同时操作电脑。
  测试宿主使用真实 ReaderWindow、Protection、HotkeyManager，所有正文均为合成测试行。
  它不加载用户配置，不调用模型，不读写剪贴板，不保存密钥。
  “设置”按钮打开普通 WPF 对照窗口；它验证阅读窗口的点击/命令链，而不是完整 MainWindow 的初始化。
  Playwright 默认强制页面处于焦点/可见状态。脚本启动独立临时浏览器，再用 connect_over_cdp(no_defaults=True) 连接默认上下文，从一开始就不启用该模拟；以失焦/隐藏正对照验证原生行为。
  Windows 输入经 SendInput（扫描码模式）注入；浏览器 isTrusted=true 仍不能说明真人硬件输入。
  测试页保持正常 DOM 事件行为，不注入防检测脚本。legacy 模式使用原有组合键；exclusive 模式由 Superb 安装限定 F6/F7/F8 的系统输入钩子。
  必须确认前台 HWND、页面焦点/可见性、显示保护和热键注册，才能开始采样。
  动作证据：可见状态、鼠标计数、实际滚动偏移、快捷键命令记录。
  环境失败报告 BLOCKED；记录器单测通过仅标 RECORDER_ONLY_PASS；有效原生测量标 MEASURED。
  发现失焦或键盘事件本身不导致测试失败，它是本实验要记录的结果。

跨平台记录器自测（不能证明 Windows 焦点行为）
  python -m pip install playwright==1.64.0
  python -m playwright install chromium
  python tools/event-probe/run_probe.py --page-only --browser chromium --output artifacts/event-probe/recorder

报告文件
  results.json：浏览器版本、OS、原生动作前后状态、事件明细、前提条件。
  report.html：可离线打开的矩阵报告。
  test-page.png：浏览器页面截图，不是 Windows 桌面截图，不证明浮窗是否被录屏捕获。

依据
  https://developer.mozilla.org/en-US/docs/Web/API/Page_Visibility_API
  https://developer.mozilla.org/en-US/docs/Web/API/Element/keydown_event
  https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
  https://playwright.dev/python/docs/browsers

自动化默认值依据：
  https://github.com/microsoft/playwright/blob/main/packages/playwright-core/src/server/chromium/crPage.ts
  https://chromedevtools.github.io/devtools-protocol/tot/Emulation/#method-setFocusEmulationEnabled

独占阅读快捷键复测
  在“高级设置 → 偏好与快捷键”勾选独占阅读快捷键并保存。默认关闭。
  F6 显示/收起，F7 向下，F8 向上；单独按键时其他应用暂时失去这三个键的功能。
  带 Ctrl/Alt/Shift/Win 的组合保持透传。其他动作仍使用原组合键，不承诺没有网页事件。
  松开所有键后再切换模式或退出；在按键周期中途卸载钩子不能保证收尾事件被消费。
  python tools/event-probe/run_probe.py --browser msedge --reader-keys exclusive --repeats 3 --output artifacts/event-probe/msedge-exclusive
  python tools/event-probe/run_probe.py --browser chrome --reader-keys exclusive --repeats 3 --output artifacts/event-probe/chrome-exclusive
  exclusive 模式有 22 个场景，包含长按去重、Ctrl+A、Shift+F8、修饰键释放顺序、关闭与重新开启；另做 F6 注册冲突与释放检查。
  核心独占场景同时要求动作发生、按下/释放为零、失焦/隐藏为零、日志无丢弃，才能通过。
  此模式不读取或保存普通按键内容，不缓冲/重放 Ctrl 或 Alt，不读取网页。
  Windows 可能因低级钩子超时静默移除钩子；独立线程缩短回调，但不能承诺所有环境永久零事件。
  依据：https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc
