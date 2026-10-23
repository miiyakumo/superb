(() => {
  'use strict';
  const CAPACITY = 5000;
  const VISIBLE_LIMIT = 200;
  const MAX_MARKERS = 100;
  const categories = { keyboard: '键盘', pointer: '指针 / 鼠标', focus: '焦点 / 可见性', clipboard: '剪贴板', layout: '滚动 / 窗口', drag: '拖放', marker: '标记 / 周期' };
  const plan = [
    { id: 'J', title: 'Chrome 真实输入法组合与候选', action: '在单行输入框切换中文输入法，输入 ceshi，观察候选窗；选择候选后再输入字母，并另测 Esc 取消组合。', expected: '核对人工观察、composition 事件、最终合成文本与焦点；Unicode 填充或没有候选窗观察不能作为本项通过证据。' },
    { id: 'K', title: 'Chrome 合成自动补全', action: '在自动补全框输入 AL，用方向键选择 ALPHA-TEST-ONE；另开一次建议后按 Esc，再普通输入。', expected: '核对建议出现、选择值、关闭后输入和修饰键状态。此项仅覆盖 datalist，不覆盖个人资料填充或地址栏建议。' },
    { id: 'A', title: '基线与对照', action: '关闭 Superb，开始记录。在输入框输入 a、长按 a，再点击靶标并滚动页面。', expected: '核对可信的 down / up、重复事件、点击计数及滚动。无操作时仍可能有浏览器环境事件。' },
    { id: 'B', title: '普通组合键残留', action: '确认已注册 Ctrl+Alt+3 显隐、Ctrl+Alt+8 / 9 翻页；聚焦网页逐个触发，再输入 a。', expected: '核对原生动作和捕获 / 冒泡日志；注意修饰键、孤立 keyup 与后续输入。普通组合键不承诺零网页事件。' },
    { id: 'C', title: '独占单键与修饰对照', action: '临时配置并启用独占 F6 / F7 / F8。分别按下、长按、释放，再按 Shift+F7；完成后恢复原绑定。', expected: '核对独占动作发生及普通单键事件是否缺失；带修饰的 Shift+F7 应交给网页。零事件不等于动作生效。' },
    { id: 'D', title: '浮窗阅读与主动编辑', action: '网页输入框保持焦点；依次显隐、更新、翻页浮窗，再点编辑和打开设置，最后返回网页。', expected: '分别核对 window focus / blur、控件 focusin / out 与 visibility。失焦和页面隐藏是不同事实，不可仅凭事件归因为 Superb。' },
    { id: 'E', title: '两点截图与取消恢复', action: '让浮窗避开点击靶标。启动两点截图，在网页靶标上选两个对角；再分别右键取消、Esc 取消，最后普通点击一次。', expected: '选点点击不应增加底层靶标计数；取消后普通点击应恢复。页面无法直接确认桌面截图或截图保护，须另行核对原生结果。' },
    { id: 'F', title: '穿透与命中范围', action: '将浮窗覆盖靶标。分别在非穿透和穿透模式点击、滚轮，再用已注册编辑键退出穿透。', expected: '比较点击计数、pointer / click / wheel 与页面 scroll。浮窗位置和模式需人工记录；网页无法直接识别覆盖窗口。' },
    { id: 'G', title: '剪贴板观测边界', action: '只用合成文本，主动复制、剪切、粘贴到测试框；再明确触发 Superb 读剪贴板。', expected: '页面只能观察自己的 copy / cut / paste 事件元信息。桌面应用后台读取剪贴板没有可据此依赖的网页事件。' },
    { id: 'H', title: '全屏与设置焦点', action: '用 F11 进入浏览器全屏，再打开 Superb 设置；返回并退出全屏。另用网页全屏按钮测试 Fullscreen API。', expected: 'F11 浏览器全屏未必触发 fullscreenchange；API 全屏与窗口 blur 应独立观察。没有事件不能证明未退出全屏。' },
    { id: 'I', title: '记录器自身边界', action: '暂停后操作，再继续；使用合成事件或足够长的测试超过日志上限；手动导出 JSON。', expected: '暂停不追加原始事件；5000 条环形日志覆盖旧记录并保留计数；导出只含本轮内存状态。dispatchEvent 合成事件必须标为 isTrusted=false。' }
  ];
  const $ = id => document.getElementById(id);
  const environment = {
    userAgent: navigator.userAgent,
    language: navigator.language,
    platform: navigator.userAgentData?.platform || navigator.platform,
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    url: location.protocol === 'file:' ? 'file:// (路径省略)' : location.origin + location.pathname,
    screen: { width: screen.width, height: screen.height, availableWidth: screen.availWidth, availableHeight: screen.availHeight },
    devicePixelRatio,
    pointerEvents: 'PointerEvent' in window,
    fullscreenEnabled: Boolean(document.fullscreenEnabled),
    secureContext: isSecureContext,
    limitations: [ '不能检测其他进程、全局钩子、桌面截图、保护置顶窗或后台剪贴板读取', '不能由 blur / 缺失按键 / 零事件单独归因 Superb', 'F11 浏览器全屏与 Fullscreen API 状态不同', '浏览器和系统保留键可能改变事件行为', '普通组合键可能将修饰键传入页面' ]
  };
  const store = new InteractionProbeCore.RingBuffer(CAPACITY);
  const eventCounter = new InteractionProbeCore.EventCounter();
  const cycles = new InteractionProbeCore.KeyCycles();
  let sequence = 0, recording = false;
  let sessionStart = performance.now(), wallStart = new Date().toISOString();
  let throttled = 0, blurCount = 0, pairCount = 0, orphanUps = 0, interrupted = 0, clickCount = 0;
  let dirty = true, markersDirty = true, lastPaint = 0, lastWarning = '';
  let markers = [], importedAudit = null, importRevision = 0;
  const held = cycles.held, lastSample = new Map(), selections = new Set(Object.keys(categories));
  const emptyCaseState = () => ({ status: 'pending', note: '', nativeActionConfirmed: false, recordingAtStart: false, recordingInterrupted: false, starts: 0, lastStartedAt: null, finishedAt: null });
  const caseState = new Map(plan.map(test => [test.id, emptyCaseState()]));
  const initialText = '合成文本 ALPHA 123\n第二行 BETA 456\n选择这一段进行复制与粘贴测试。';

  function entries() {
    return store.snapshot();
  }
  function targetName(target) {
    if (target === window) return 'window';
    if (target === document) return 'document';
    if (!(target instanceof Element)) return target?.nodeName || 'unknown';
    let result = target.tagName.toLowerCase();
    if (target.id) result += '#' + target.id.slice(0, 80);
    else if (target.classList.length) result += '.' + [...target.classList].slice(0, 2).join('.').slice(0, 80);
    return result;
  }
  function currentState() {
    return { hasFocus: document.hasFocus(), visibility: document.visibilityState, activeElement: targetName(document.activeElement), fullscreen: Boolean(document.fullscreenElement), pointerLock: Boolean(document.pointerLockElement), viewport: { width: innerWidth, height: innerHeight }, scroll: { x: Math.round(scrollX), y: Math.round(scrollY) } };
  }
  function append(type, category, phase, event, details) {
    const record = {
      id: ++sequence, timestamp: new Date().toISOString(), elapsedMs: Math.round(performance.now() - sessionStart), type, category, phase,
      isTrusted: event ? Boolean(event.isTrusted) : null,
      target: event ? targetName(event.target) : 'probe',
      defaultPrevented: event ? event.defaultPrevented : null,
      ...details
    };
    store.push(record);
    dirty = true;
    return record;
  }
  function observe(event, category, phase, details, interval = 0) {
    if (!recording) return;
    eventCounter.observe(event, phase);
    const now = performance.now(), throttleKey = event.type + ':' + phase;
    if (interval && now - (lastSample.get(throttleKey) ?? -Infinity) < interval) { throttled++; dirty = true; return; }
    if (interval) lastSample.set(throttleKey, now);
    append(event.type, category, phase, event, details);
  }
  function mark(label, details = {}) {
    const item = { timestamp: new Date().toISOString(), elapsedMs: Math.round(performance.now() - sessionStart), label: String(label).slice(0, 500), ...details };
    if (markers.length >= MAX_MARKERS) markers.shift();
    markers.push(item);
    append('marker', 'marker', 'observe', null, { ...item, whilePaused: !recording });
    markersDirty = true;
  }
  function keyDetails(event) {
    return { key: String(event.key).slice(0, 80), code: String(event.code).slice(0, 80), repeat: event.repeat, location: event.location, modifiers: { ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, meta: event.metaKey, altGraph: event.getModifierState?.('AltGraph') || false }, isComposing: event.isComposing };
  }
  function trackCycle(event) {
    if (!recording) return;
    const result = cycles.accept(event, performance.now());
    if (result.kind === 'paired') {
      pairCount++;
      append('key-cycle-complete', 'marker', 'observe', event, { code: InteractionProbeCore.boundedKey(event.code), key: InteractionProbeCore.boundedKey(event.key), durationMs: result.durationMs, repeats: result.repeats });
    } else if (result.kind === 'orphan-up') {
      orphanUps++;
      append('key-cycle-note', 'marker', 'observe', event, { reason: '本轮未观察到配对 keydown；可能在开始记录前按下、曾失焦或被系统处理', code: InteractionProbeCore.boundedKey(event.code), key: InteractionProbeCore.boundedKey(event.key) });
    } else if (result.kind === 'duplicate-down') {
      append('key-cycle-note', 'marker', 'observe', event, { reason: '未见释放又收到非重复按下，保留最早按下时间', code: InteractionProbeCore.boundedKey(event.code) });
    }
    dirty = true;
  }
  function interruptCycles(reason, event) {
    if (!recording || held.size === 0) return;
    interrupted += held.size;
    append('key-cycle-interrupted', 'marker', 'observe', event, { reason, keys: [...held.values()].map(key => ({ key: key.key, code: key.code, isTrusted: key.isTrusted })), interpretation: '仅表示页面未收到完整周期，不能归因为 Superb' });
    held.clear();
    dirty = true;
  }
  for (const type of ['keydown', 'keyup']) {
    document.addEventListener(type, event => { observe(event, 'keyboard', 'capture', keyDetails(event)); trackCycle(event); }, true);
    document.addEventListener(type, event => observe(event, 'keyboard', 'bubble', keyDetails(event)), false);
  }
  const pointerTypes = ['pointerdown', 'pointerup', 'pointermove', 'pointercancel', 'pointerover', 'pointerout', 'pointerenter', 'pointerleave', 'mousedown', 'mouseup', 'mousemove', 'mouseover', 'mouseout', 'mouseenter', 'mouseleave', 'click', 'dblclick', 'contextmenu', 'wheel'];
  for (const type of pointerTypes) {
    for (const capture of [true, false]) {
      document.addEventListener(type, event => observe(event, 'pointer', capture ? 'capture' : 'bubble', {
        button: event.button, buttons: event.buttons, x: Math.round(event.clientX), y: Math.round(event.clientY),
        pointerType: event.pointerType, pointerId: event.pointerId,
        modifiers: { ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, meta: event.metaKey },
        ...(type === 'wheel' ? { deltaX: Math.round(event.deltaX), deltaY: Math.round(event.deltaY), deltaMode: event.deltaMode } : {})
      }, ['pointermove', 'mousemove', 'wheel'].includes(type) ? 50 : 0), { capture, passive: true });
    }
  }
  for (const type of ['focus', 'blur']) {
    window.addEventListener(type, event => {
      if (!recording) return;
      if (type === 'blur') { blurCount++; interruptCycles('window blur 时终止本页周期跟踪', event); }
      observe(event, 'focus', 'observe', currentState());
    });
  }
  for (const type of ['focusin', 'focusout']) document.addEventListener(type, event => observe(event, 'focus', 'capture', { relatedTarget: targetName(event.relatedTarget), ...currentState() }), true);
  document.addEventListener('visibilitychange', event => {
    observe(event, 'focus', 'observe', currentState());
    if (document.hidden) interruptCycles('页面隐藏时终止本页周期跟踪', event);
  });
  for (const type of ['beforeinput', 'input', 'compositionstart', 'compositionupdate', 'compositionend']) document.addEventListener(type, event => observe(event, 'keyboard', 'capture', {
    inputType: event.inputType ? String(event.inputType).slice(0, 80) : null,
    isComposing: Boolean(event.isComposing), dataLength: typeof event.data === 'string' ? event.data.length : null,
    valueLength: event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement ? event.target.value.length : null,
    contentsStored: false
  }), true);
  for (const type of ['pageshow', 'pagehide', 'online', 'offline']) window.addEventListener(type, event => observe(event, 'focus', 'observe', { persisted: event.persisted, online: navigator.onLine, ...currentState() }));
  if (window.visualViewport) for (const type of ['resize', 'scroll']) window.visualViewport.addEventListener(type, event => observe(event, 'layout', 'observe', { source: 'visualViewport', width: Math.round(visualViewport.width), height: Math.round(visualViewport.height), scale: visualViewport.scale, offsetLeft: Math.round(visualViewport.offsetLeft), offsetTop: Math.round(visualViewport.offsetTop) }, 150), { passive: true });
  document.addEventListener('selectionchange', event => observe(event, 'layout', 'observe', selectionDetails(), 100));
  function selectionDetails() {
    const active = document.activeElement;
    if (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) {
      const start = active.selectionStart, end = active.selectionEnd;
      return { selectionTarget: targetName(active), length: start === null || end === null ? 0 : Math.abs(end - start), rangeCount: null };
    }
    const selection = getSelection();
    return { selectionTarget: targetName(selection?.anchorNode?.parentElement), length: selection?.toString().length || 0, rangeCount: selection?.rangeCount || 0 };
  }
  for (const type of ['copy', 'cut', 'paste']) document.addEventListener(type, event => {
    if (!recording) return;
    observe(event, 'clipboard', 'capture', InteractionProbeCore.clipboardMetadata(event, selectionDetails().length));
  }, true);
  for (const type of ['resize', 'scroll']) window.addEventListener(type, event => {
    // Scrolling diagnostic output must not recursively generate its own output.
    if (['log-viewport', 'audit-viewport', 'correlation-viewport', 'markers', 'json-preview'].includes(event.target?.id)) return;
    observe(event, 'layout', 'observe', { ...currentState(), targetScroll: event.target instanceof Element ? { x: Math.round(event.target.scrollLeft), y: Math.round(event.target.scrollTop) } : null }, type === 'resize' ? 150 : 100);
  }, { capture: true, passive: true });
  for (const type of ['fullscreenchange', 'fullscreenerror', 'pointerlockchange', 'pointerlockerror']) document.addEventListener(type, event => observe(event, 'layout', 'observe', currentState()));
  for (const type of ['dragstart', 'dragenter', 'dragleave', 'dragover', 'drop', 'dragend']) document.addEventListener(type, event => observe(event, 'drag', 'capture', { types: InteractionProbeCore.boundedTypes(event.dataTransfer?.types), fileCount: event.dataTransfer?.files.length || 0, dropEffect: event.dataTransfer?.dropEffect, contentsStored: false }, type === 'dragover' ? 150 : 0), true);
  // Suppress only the explicit sandbox drop target's default file-opening behavior.
  for (const type of ['dragenter', 'dragover', 'drop']) $('drop-target').addEventListener(type, event => { event.preventDefault(); $('drop-target').classList.toggle('over', type !== 'drop'); });
  $('drop-target').addEventListener('dragleave', () => $('drop-target').classList.remove('over'));

  $('toggle-recording').addEventListener('click', () => {
    if (recording) { interruptCycles('用户暂停记录', null); recording = false; for (const state of caseState.values()) if (state.lastStartedAt && !state.finishedAt) state.recordingInterrupted = true; mark('暂停记录'); }
    else { recording = true; held.clear(); lastSample.clear(); mark('开始 / 继续记录', { state: currentState() }); }
    updateRecording();
  });
  function updateRecording() {
    eventCounter.recording = recording;
    $('recording-dot').classList.toggle('active', recording);
    $('recording-state').textContent = recording ? '正在记录' : '已暂停';
    $('toggle-recording').textContent = recording ? '暂停记录' : '开始 / 继续记录';
    dirty = true;
  }
  $('clear-log').addEventListener('click', () => {
    store.clear(); eventCounter.clear(); eventCounter.recording = recording; sequence = 0;
    throttled = blurCount = pairCount = orphanUps = interrupted = 0;
    markers = []; $('json-preview').value = '';  held.clear(); lastSample.clear(); sessionStart = performance.now(); wallStart = new Date().toISOString();
    for (const value of caseState.values()) Object.assign(value, emptyCaseState());
    removeAudit();
    renderCases(); markersDirty = dirty = true;
    mark('日志已清空，开始新一轮', { clickCounterPreserved: clickCount });
  });
  $('click-target').addEventListener('click', event => {
    clickCount++;
    $('click-count').textContent = String(clickCount);
    if (recording) append('target-click-count', 'marker', 'observe', event, { count: clickCount });
    dirty = true;
  });
  $('reset-input').addEventListener('click', () => {
    $('test-autocomplete').value = '';
    $('test-input').value = 'Superb 测试 123 — 不含真实题目或密钥'; $('test-textarea').value = initialText;
    clickCount = 0; $('click-count').textContent = '0';
    mark('还原合成文本与点击计数');
  });
  $('fullscreen-toggle').addEventListener('click', async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await document.documentElement.requestFullscreen();
    } catch (error) { mark('Fullscreen API 请求失败', { error: error?.name || 'Error' }); }
  });
  $('add-marker').addEventListener('click', () => {
    const input = $('marker-note');
    if (input.value.trim()) { mark(input.value.trim()); input.value = ''; }
  });
  for (const [name, label] of Object.entries(categories)) {
    const wrapper = document.createElement('label'), checkbox = document.createElement('input');
    checkbox.type = 'checkbox'; checkbox.checked = true; checkbox.value = name;
    checkbox.addEventListener('change', () => { if (checkbox.checked) selections.add(name); else selections.delete(name); dirty = true; });
    wrapper.append(checkbox, document.createTextNode(label)); $('category-filters').append(wrapper);
  }
  for (const id of ['search-filter', 'phase-filter', 'trust-filter']) $(id).addEventListener(id === 'search-filter' ? 'input' : 'change', () => { dirty = true; });
  function renderCases() {
    const container = $('test-cases'); container.replaceChildren();
    for (const test of plan) {
      const state = caseState.get(test.id), article = document.createElement('article'); article.className = 'test-case';
      const heading = document.createElement('h3'), number = document.createElement('span'); number.className = 'case-number'; number.textContent = test.id;
      heading.append(number, document.createTextNode(test.title));
      const action = document.createElement('p'); action.className = 'case-instruction'; action.textContent = test.action;
      const expectation = document.createElement('p'); expectation.className = 'case-expectation'; expectation.textContent = '核对：' + test.expected;
      const controls = document.createElement('div'); controls.className = 'case-tools';
      const button = document.createElement('button'); button.textContent = '开始本项'; button.id = `case-${test.id}-start`;
      button.addEventListener('click', () => { state.starts++; state.lastStartedAt = new Date().toISOString(); state.finishedAt = null; state.recordingAtStart = recording; state.recordingInterrupted = false; state.nativeActionConfirmed = false; state.status = 'pending'; state.note = ''; status.value = 'pending'; native.checked = false; note.value = ''; mark(`${test.id} · ${test.title} 开始`, { caseId: test.id, iteration: state.starts }); updateProgress(); });
      const status = document.createElement('select'); status.id = `case-${test.id}-status`; status.setAttribute('aria-label', test.title + ' 核对结果');
      for (const [value, label] of [['pending', '待测'], ['checked', '已核对，符合预期'], ['issue', '观察到异常'], ['inconclusive', '证据不足 / 未执行']]) {
        const option = document.createElement('option'); option.value = value; option.textContent = label; status.append(option);
      }
      status.value = state.status;
      status.addEventListener('change', () => { state.status = status.value; state.finishedAt = status.value === 'pending' ? null : new Date().toISOString(); mark(`${test.id} · ${test.title} 人工结果：${status.selectedOptions[0].textContent}`, { caseId: test.id }); updateProgress(); });
      const note = document.createElement('input'); note.className = 'case-note'; note.maxLength = 500; note.value = state.note; note.placeholder = '原生动作证据 / 网页现象（仅测试内容）'; note.setAttribute('aria-label', test.title + ' 备注');
      note.addEventListener('input', () => { state.note = note.value; dirty = true; });
      const nativeLabel = document.createElement('label'), native = document.createElement('input'); nativeLabel.className = 'inline-check native-confirm'; native.type = 'checkbox'; native.checked = state.nativeActionConfirmed; native.id = `case-${test.id}-native`;
      native.addEventListener('change', () => { state.nativeActionConfirmed = native.checked; mark(`${test.id} · 原生动作人工确认：${native.checked ? '已发生' : '未确认'}`, { caseId: test.id }); });
      nativeLabel.append(native, document.createTextNode('已在原生界面确认本项动作发生（人工声明）'));
      controls.append(button, status); article.append(heading, action, expectation, controls, nativeLabel, note); container.append(article);
    }
    updateProgress();
  }
  function updateProgress() { $('case-progress').textContent = `${[...caseState.values()].filter(value => value.status !== 'pending').length} / ${plan.length} 已记录`; }
  function evidenceReview() {
    const currentEvents = entries(), now = new Date().toISOString();
    return plan.map(test => ({ caseId: test.id, manualStatus: caseState.get(test.id).status, ...InteractionProbeAudit.correlateCase(caseState.get(test.id), currentEvents, importedAudit, now, store.evicted) }));
  }
  function renderCorrelation() {
    const fragment = document.createDocumentFragment(), statuses = { pending: '待测', checked: '人工声明符合预期', issue: '人工观察异常', inconclusive: '人工声明证据不足' };
    for (const review of evidenceReview()) {
      const row = document.createElement('tr');
      for (const value of [review.caseId + ' · ' + statuses[review.manualStatus], `${review.browserRecords} 行（可信 ${review.trustedBrowserRecords} / 合成 ${review.syntheticBrowserRecords}）`, `${review.auditSamples} 个`, (review.status === 'insufficient' ? '证据不足：' + review.reasons.join('；') : '证据已并列，仍需人工复核；不等于通过')]) {
        const cell = document.createElement('td'); cell.textContent = value; row.append(cell);
      }
      fragment.append(row);
    }
    $('correlation-rows').replaceChildren(fragment);
    $('browser-evidence-summary').textContent = `${eventCounter.observed} 个已观察事件 · 保留 ${store.size} 行 · 覆盖 ${store.evicted} 行 · ${recording ? '正在记录' : '已暂停'}。零事件不代表动作成功。`;
  }
  function removeAudit() {
    importRevision++; importedAudit = null; $('audit-file').value = ''; $('audit-message').textContent = '未导入本机窗口证据。'; renderAudit(); dirty = true;
  }
  $('audit-clear').addEventListener('click', removeAudit);
  // Audits are often regenerated at the same path. Permit re-selecting that file.
  $('audit-file').addEventListener('click', event => { event.currentTarget.value = ''; });
  $('audit-file').addEventListener('change', async event => {
    const file = event.target.files?.[0], revision = ++importRevision;
    if (!file) return;
    try {
      if (file.size > InteractionProbeAudit.LIMITS.bytes) throw new TypeError('窗口审计 JSON：文件不能超过 4 MiB');
      const parsed = InteractionProbeAudit.parseAudit(await file.text());
      if (revision !== importRevision) return;
      importedAudit = parsed;
      $('audit-message').textContent = '导入完成，仅保存在本页内存。文件内容经过结构校验，来源声明仍需独立核实。';
      renderAudit(); dirty = true;
      mark('用户导入本机窗口审计', { auditSchema: parsed.schema, snapshots: parsed.snapshots.length, synthetic: parsed.source.synthetic });
    } catch (error) {
      if (revision !== importRevision) return;
      $('audit-message').textContent = `${error.message}。${importedAudit ? '保留上一次成功导入的数据。' : '未导入数据。'}`;
    }
  });
  $('audit-target-only').addEventListener('change', renderAuditWindows);
  $('audit-snapshot').addEventListener('change', renderAuditWindows);
  function renderAudit() {
    const select = $('audit-snapshot'); select.replaceChildren(); select.disabled = !importedAudit?.snapshots.length;
    const options = importedAudit?.snapshots || [];
    if (!options.length) { const option = document.createElement('option'); option.textContent = importedAudit ? '没有快照' : '尚未导入'; select.append(option); }
    options.forEach((sample, index) => { const option = document.createElement('option'); option.value = String(index); option.textContent = `${index + 1} · ${sample.timestamp} · ${sample.windows.length} 窗口`; select.append(option); });
    if (options.length) select.value = String(options.length - 1);
    $('audit-state').textContent = importedAudit ? (importedAudit.source.synthetic ? '合成宿主 / 示例' : '导入的本机记录') : '尚未导入';
    $('audit-summary').textContent = importedAudit ? `${importedAudit.snapshots.length} 个快照 / ${importedAudit.observations} 条窗口观测。${importedAudit.startedAt} 至 ${importedAudit.endedAt}。` : '未导入本机窗口证据。';
    $('audit-source').textContent = importedAudit ? JSON.stringify({ source: importedAudit.source, notes: importedAudit.notes, capturedAt: importedAudit.capturedAt }, null, 2) : '未导入';
    renderAuditWindows();
  }
  function renderAuditWindows() {
    const sample = importedAudit?.snapshots[Number($('audit-snapshot').value)], targetOnly = $('audit-target-only').checked;
    const windows = (sample?.windows || []).filter(window => !targetOnly || window.isTarget !== false), fragment = document.createDocumentFragment();
    // Bound DOM work even for valid files with a large single snapshot.
    for (const window of windows.slice(0, 500)) {
      const row = document.createElement('tr'), features = InteractionProbeAudit.windowFeatures(window, sample.foregroundHwnd);
      const affinity = window.displayAffinity == null ? `未知 / 不可读${window.affinityError != null ? `（错误 ${window.affinityError}）` : ''}` : window.displayAffinity === 0 ? '0 · WDA_NONE' : window.displayAffinity === 1 ? '1 · WDA_MONITOR' : window.displayAffinity === 17 ? '17 · WDA_EXCLUDEFROMCAPTURE' : String(window.displayAffinity);
      const rect = window.rect ? JSON.stringify(window.rect) : '未知';
      const values = [
        `${window.hwnd} · PID ${window.processId ?? '未知'} · ${window.processName || '进程名未采集'} · ${window.isTarget === true ? '目标' : window.isTarget === false ? '其他进程' : '目标归属未知'}`,
        `${window.title || '（标题为空或未采集；不能据此认定无标题）'} / ${window.className || '类名未知'}`,
        features.length ? features.join(' · ') : '未观察到所列样式；仍需人工复核',
        `显示亲和性：${affinity} · 范围：${rect} · owner：${window.ownerHwnd || '未知 / 无'} · style：${window.style ?? '未知'} · exStyle：${window.exStyle ?? '未知'}`
      ];
      for (const value of values) { const cell = document.createElement('td'); cell.textContent = value; row.append(cell); }
      fragment.append(row);
    }
    if (!windows.length || windows.length > 500) { const row = document.createElement('tr'), cell = document.createElement('td'); cell.colSpan = 4; cell.className = 'empty'; cell.textContent = windows.length > 500 ? `当前快照匹配 ${windows.length} 个窗口，仅显示前 500 个；完整数据保留在导出中。` : '当前筛选或快照没有窗口；不能据此认定没有窗口或通过测试。'; row.append(cell); fragment.append(row); }
    $('audit-windows').replaceChildren(fragment);
  }
  function summary() {
    return { observed: eventCounter.observed, retained: store.size, capacity: CAPACITY, evicted: store.evicted, throttled, windowBlur: blurCount, completedKeyPairs: pairCount, unmatchedKeyUps: orphanUps, interruptedKeyCycles: interrupted, pendingKeys: [...held.values()].map(key => ({ key: key.key, code: key.code, isTrusted: key.isTrusted, durationMs: Math.round(performance.now() - key.downAt), repeats: key.repeats })), targetClicks: clickCount, byType: { ...eventCounter.byPhase } };
  }
  function snapshot() {
    return structuredClone({
      schema: 'superb-interaction-probe/v1', createdAt: wallStart, exportedAt: new Date().toISOString(),
      mode: 'local-browser-observation', recording, environment: { ...environment }, state: currentState(),
      summary: summary(), testPlan: plan.map(test => ({ ...test, result: { ...caseState.get(test.id) } })), markers: structuredClone(markers), events: structuredClone(entries()),
      importedWindowAudit: importedAudit, evidenceReview: evidenceReview(),
      evidenceBoundary: '网页事件、本机导入、人工声明属于独立证据。只按时间并列，不建立因果，不自动通过。网页本身不能枚举窗口；零事件不能证明原生动作成功。合成事件由 isTrusted=false 标识。'
    });
  }
  Object.defineProperty(window, 'interactionProbe', { value: Object.freeze({ snapshot }), writable: false, configurable: false });
  $('export-log').addEventListener('click', () => {
    mark('用户导出 JSON');
    const blob = new Blob([JSON.stringify(snapshot(), null, 2)], { type: 'application/json;charset=utf-8' });
    const url = URL.createObjectURL(blob), anchor = document.createElement('a');
    anchor.href = url; anchor.download = `superb-interaction-probe-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
    anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  $('preview-json').addEventListener('click', () => {
    $('json-preview').value = JSON.stringify(snapshot(), null, 2);
    $('json-details').open = true;
  });
  function detailString(item) {
    const skip = new Set(['id', 'timestamp', 'elapsedMs', 'type', 'category', 'phase', 'target']);
    return Object.entries(item).filter(([name, value]) => !skip.has(name) && value !== undefined && value !== null).map(([name, value]) => `${name}=${typeof value === 'object' ? JSON.stringify(value) : value}`).join(' · ');
  }
  function renderLog() {
    const search = $('search-filter').value.toLocaleLowerCase(), phase = $('phase-filter').value, trust = $('trust-filter').value;
    const filtered = entries().filter(item => selections.has(item.category) && (phase === 'all' || item.phase === phase) && (trust === 'all' || String(item.isTrusted) === trust) && (!search || JSON.stringify(item).toLocaleLowerCase().includes(search)));
    $('filter-summary').textContent = `${filtered.length} 条匹配${filtered.length > VISIBLE_LIMIT ? ' · 显示最近 200 条' : ''}`;
    const body = $('event-log'), fragment = document.createDocumentFragment();
    for (const item of filtered.slice(-VISIBLE_LIMIT)) {
      const row = document.createElement('tr'); row.dataset.category = item.category;
      for (const text of [`+${(item.elapsedMs / 1000).toFixed(3)}s`, `${item.type} / ${item.phase}`, item.target, detailString(item)]) {
        const cell = document.createElement('td'); cell.textContent = text; row.append(cell);
      }
      fragment.append(row);
    }
    if (filtered.length === 0) { const row = document.createElement('tr'), cell = document.createElement('td'); cell.colSpan = 4; cell.className = 'empty'; cell.textContent = '没有匹配事件。检查是否正在记录、焦点位置与筛选条件。'; row.append(cell); fragment.append(row); }
    const viewport = $('log-viewport'), previousTop = viewport.scrollTop;
    const atBottom = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 40;
    body.replaceChildren(fragment);
    if ($('auto-scroll').checked && atBottom) viewport.scrollTop = viewport.scrollHeight;
    else viewport.scrollTop = previousTop;
    const counts = $('event-counts'); counts.replaceChildren();
    for (const [type, count] of Object.entries(eventCounter.byPhase).sort(([a], [b]) => a.localeCompare(b))) { const node = document.createElement('span'); node.textContent = `${type} ${count}`; counts.append(node); }
  }
  function renderMarkers() {
    const list = $('markers'); list.replaceChildren();
    for (const item of markers.slice(-30).reverse()) { const node = document.createElement('li'); node.textContent = `+${(item.elapsedMs / 1000).toFixed(2)}s · ${item.label}`; list.append(node); }
    if (!markers.length) { const node = document.createElement('li'); node.textContent = '暂无标记'; list.append(node); }
    markersDirty = false;
  }
  function paint() {
    const elapsed = Math.floor((performance.now() - sessionStart) / 1000);
    $('elapsed').textContent = `${String(Math.floor(elapsed / 60)).padStart(2, '0')}:${String(elapsed % 60).padStart(2, '0')}`;
    const pending = [...held.values()], longHeld = pending.filter(key => performance.now() - key.downAt > 3000);
    const warning = `${orphanUps} 次未配对释放 · ${interrupted} 次因失焦 / 暂停中断${longHeld.length ? ` · ${longHeld.length} 个键已超过 3 秒未见释放（也可能正在长按）` : ''}`;
    if (warning !== lastWarning) { $('cycle-warning').textContent = warning + '。均需人工核对，不能归因为 Superb。'; $('cycle-warning').classList.toggle('warning', orphanUps > 0 || interrupted > 0 || longHeld.length > 0); lastWarning = warning; }
    $('key-state').textContent = pending.length ? pending.map(key => `${key.code || key.key} ${key.isTrusted ? '[可信]' : '[合成]'} ${((performance.now() - key.downAt) / 1000).toFixed(1)}s`).join(' · ') : '当前未观察到仍按住的键。';
    if (markersDirty) renderMarkers();
    if (!dirty || performance.now() - lastPaint < 200) return;
    $('stat-observed').textContent = eventCounter.observed.toLocaleString(); $('stat-retained').textContent = store.size.toLocaleString(); $('stat-evicted').textContent = `覆盖 ${store.evicted.toLocaleString()} 条旧记录`;
    $('stat-throttled').textContent = throttled.toLocaleString(); $('stat-blur').textContent = String(blurCount); $('stat-pairs').textContent = pairCount.toLocaleString(); $('stat-pending').textContent = `${held.size} 个待释放 · ${orphanUps} 次未配对释放`;
    dirty = false; lastPaint = performance.now(); renderLog(); renderCorrelation();
  }
  $('environment').textContent = JSON.stringify(environment, null, 2);
  renderCases(); renderAudit(); setInterval(paint, 250); paint();
})();
