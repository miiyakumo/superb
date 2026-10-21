'use strict';
(() => {
  let active = null, timer = null;
  const trials = [], MAX_EVENTS = 5000;
  const $ = id => document.getElementById(id);
  const state = () => ({hasFocus: document.hasFocus(), visibility: document.visibilityState});
  const target = value => value === window ? 'window' : value === document ? 'document' : value?.id === 'pad' ? '#pad' : value?.tagName || 'unknown';
  function counts(events) {
    return {windowBlur: events.filter(e => e.type === 'window.blur').length,
      windowFocus: events.filter(e => e.type === 'window.focus').length,
      hidden: events.filter(e => e.type === 'visibilitychange' && e.visibility === 'hidden').length,
      keydown: events.filter(e => e.type === 'keydown').length,
      keyup: events.filter(e => e.type === 'keyup').length,
      untrusted: events.filter(e => !e.isTrusted).length};
  }
  function render() {
    const s = state(); $('focus').textContent = '焦点：' + (s.hasFocus ? '页面持有' : '页面未持有');
    $('visibility').textContent = '可见性：' + s.visibility;
    $('recording').textContent = active ? '正在记录 · ' + active.label : '未记录';
    const run = active || trials.at(-1); if (!run) return;
    const c = counts(run.events);
    $('summary').textContent = `窗口失焦 ${c.windowBlur} 次 · 隐藏 ${c.hidden} 次 · 按下 ${c.keydown} 次 · 松开 ${c.keyup} 次 · 非可信 ${c.untrusted} 次 · 丢弃 ${run.dropped} 条`;
    const fragment = document.createDocumentFragment();
    for (const e of run.events.slice(-100)) {
      const row = document.createElement('tr');
      for (const value of [e.ms,e.type,e.target,e.code || '—',e.modifiers.join('+') || '—',String(e.isTrusted),`${e.hasFocus} / ${e.visibility}`]) {
        const td = document.createElement('td'); td.textContent = value; row.append(td);
      }
      fragment.append(row);
    }
    $('events').replaceChildren(fragment);
  }
  function record(type, e) {
    if (!active) { render(); return; }
    if (active.events.length >= MAX_EVENTS) { active.dropped++; return; }
    active.events.push({ms: Math.round((performance.now() - active.monotonicStart)*100)/100,
      type, target: target(e.target), code: e.code || null,
      modifiers: ['ctrl','alt','shift','meta'].filter(k => e[k+'Key']),
      repeat: !!e.repeat, isTrusted: e.isTrusted, ...state()});
    render();
  }
  function stop() {
    clearTimeout(timer); timer = null;
    if (active) { active.endedAt = new Date().toISOString(); active.endState = state(); active.counts = counts(active.events); trials.push(active); active = null; }
    render(); return snapshot();
  }
  function start(label, durationMs = 0) {
    if (active) stop();
    active = {label: String(label).slice(0,160), startedAt: new Date().toISOString(), monotonicStart: performance.now(), initialState: state(), events: [], dropped: 0};
    if (durationMs > 0) timer = setTimeout(stop, Math.min(durationMs,60000));
    render(); return state();
  }
  function snapshot() { return JSON.parse(JSON.stringify({schema: 1, userAgent: navigator.userAgent, platform: navigator.platform, exportedAt: new Date().toISOString(), trials, active})); }
  const escape = x => String(x).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  function report() {
    const data = snapshot(); const runs = [...data.trials, ...(data.active ? [data.active] : [])];
    const rows = runs.map(r => { const c=counts(r.events); return `<tr><td>${escape(r.label)}</td><td>${escape(JSON.stringify(r.initialState))}</td><td>${c.windowBlur}</td><td>${c.hidden}</td><td>${c.keydown}/${c.keyup}</td><td>${r.dropped}</td></tr>`; }).join('');
    return `<!doctype html><meta charset="utf-8"><title>Superb 网页事件报告</title><style>body{font:16px/1.7 system-ui;max-width:1100px;margin:40px auto;padding:20px}table{border-collapse:collapse}td,th{border:1px solid #bbb;padding:10px}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style><h1>Superb 网页事件报告</h1><p>浏览器：${escape(data.userAgent)}</p><p>本报告仅记录网页收到的事件；没有自动确认浮窗操作实际执行。无事件不等于不可检测。导出时间：${escape(data.exportedAt)}</p><table><tr><th>场景</th><th>开始状态</th><th>窗口失焦</th><th>隐藏</th><th>按下/松开</th><th>丢弃</th></tr>${rows}</table><details><summary>原始记录</summary><pre>${escape(JSON.stringify(data,null,2))}</pre></details>`;
  }
  function download(name, content, type) { const url=URL.createObjectURL(new Blob([content],{type})); const a=document.createElement('a'); a.href=url; a.download=name; a.click(); setTimeout(()=>URL.revokeObjectURL(url),1000); }
  for (const type of ['blur','focus']) window.addEventListener(type,e=>{if(e.target===window)record('window.'+type,e);});
  document.addEventListener('visibilitychange',e=>record('visibilitychange',e));
  for (const type of ['focusin','focusout','keydown','keyup','pointerdown','wheel']) document.addEventListener(type,e=>record(type,e),{capture:true,passive:true});
  $('start').addEventListener('click',()=>{ $('pad').focus(); start($('scenario').value,10000); });
  $('stop').addEventListener('click',stop);
  $('json').addEventListener('click',()=>download('superb-events.json',JSON.stringify(snapshot(),null,2),'application/json'));
  $('report').addEventListener('click',()=>download('superb-event-report.html',report(),'text/html'));
  window.eventProbe = Object.freeze({start,stop,snapshot,report}); render();
})();
