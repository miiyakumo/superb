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
  Windows 输入经 SendInput 注入；浏览器 isTrusted=true 仍不能说明真人硬件输入。
  页面、鼠标、快捷键均保持正常系统行为，没有注入网页防检测脚本或安装输入钩子。
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
