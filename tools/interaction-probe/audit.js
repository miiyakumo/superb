(function (root) {
  'use strict';
  const LIMITS = Object.freeze({ bytes: 4 * 1024 * 1024, snapshots: 500, observations: 20000, text: 512 });
  const schema = 'superb-window-audit/v1';
  function fail(message) { throw new TypeError('窗口审计 JSON：' + message); }
  function object(value, label) { if (!value || typeof value !== 'object' || Array.isArray(value)) fail(label + ' 必须是对象'); return value; }
  function text(value, label, required = false) {
    if (value == null && !required) return '';
    if (typeof value !== 'string' || value.length > LIMITS.text || (required && !value.trim())) fail(label + ' 必须是 1–512 字符文本');
    return value;
  }
  function timestamp(value, label) {
    if (typeof value !== 'string' || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$/.test(value) || !Number.isFinite(Date.parse(value))) fail(label + ' 必须是带时区的 ISO 时间');
    return new Date(value).toISOString();
  }
  function optionalBoolean(value, label) { if (value == null) return null; if (typeof value !== 'boolean') fail(label + ' 必须是 boolean 或 null'); return value; }
  function optionalNumber(value, label) { if (value == null) return null; if (!Number.isSafeInteger(value)) fail(label + ' 必须是整数或 null'); return value; }
  function parseAudit(input) {
    if (typeof input !== 'string' || new TextEncoder().encode(input).byteLength > LIMITS.bytes) fail('文件不能超过 4 MiB');
    let raw; try { raw = JSON.parse(input); } catch { fail('无法解析；只接受 JSON，不接受脚本或 HTML'); }
    object(raw, '根节点');
    if (raw.schema !== schema) fail('不支持的 schema；需要 ' + schema);
    const source = object(raw.source, 'source');
    if (typeof source.synthetic !== 'boolean') fail('source.synthetic 必须明确为 true 或 false');
    if (!Array.isArray(raw.snapshots) || raw.snapshots.length > LIMITS.snapshots) fail('snapshots 必须是数组，最多 500 项');
    let observations = 0;
    const snapshots = raw.snapshots.map((item, index) => {
      object(item, 'snapshot');
      if (!Array.isArray(item.windows)) fail('snapshot.windows 必须是数组');
      observations += item.windows.length;
      if (observations > LIMITS.observations) fail('窗口观测总数不能超过 20,000');
      return {
        timestamp: timestamp(item.timestamp, `snapshots[${index}].timestamp`),
        foregroundHwnd: item.foregroundHwnd == null ? null : text(item.foregroundHwnd, 'foregroundHwnd', true),
        windows: item.windows.map(window => {
          object(window, 'window');
          let rect = null;
          if (window.rect != null) {
            object(window.rect, 'rect');
            rect = Object.fromEntries(['left', 'top', 'right', 'bottom'].map(key => [key, optionalNumber(window.rect[key], 'rect.' + key)]));
          }
          return {
            hwnd: text(window.hwnd, 'hwnd', true), processId: optionalNumber(window.processId, 'processId'), isTarget: optionalBoolean(window.isTarget, 'isTarget'),
            processName: text(window.processName, 'processName'), title: text(window.title, 'title'), className: text(window.className, 'className'),
            isVisible: optionalBoolean(window.isVisible, 'isVisible'), isTopmost: optionalBoolean(window.isTopmost, 'isTopmost'),
            isToolWindow: optionalBoolean(window.isToolWindow, 'isToolWindow'), isNoActivate: optionalBoolean(window.isNoActivate, 'isNoActivate'),
            isTransparent: optionalBoolean(window.isTransparent, 'isTransparent'), isLayered: optionalBoolean(window.isLayered, 'isLayered'), isCloaked: optionalBoolean(window.isCloaked, 'isCloaked'),
            ownerHwnd: window.ownerHwnd == null ? null : text(window.ownerHwnd, 'ownerHwnd'),
            displayAffinity: optionalNumber(window.displayAffinity, 'displayAffinity'), affinityError: optionalNumber(window.affinityError, 'affinityError'),
            style: typeof window.style === 'string' ? text(window.style, 'style') : optionalNumber(window.style, 'style'),
            exStyle: typeof window.exStyle === 'string' ? text(window.exStyle, 'exStyle') : optionalNumber(window.exStyle, 'exStyle'), rect
          };
        })
      };
    }).sort((a, b) => a.timestamp.localeCompare(b.timestamp));
    const capturedAt = timestamp(raw.capturedAt, 'capturedAt');
    const startedAt = timestamp(raw.startedAt ?? snapshots[0]?.timestamp ?? raw.capturedAt, 'startedAt');
    const endedAt = timestamp(raw.endedAt ?? snapshots.at(-1)?.timestamp ?? raw.capturedAt, 'endedAt');
    if (Date.parse(startedAt) > Date.parse(endedAt)) fail('startedAt 不能晚于 endedAt');
    if (snapshots.some(item => item.timestamp < startedAt || item.timestamp > endedAt)) fail('快照时间必须处于 startedAt / endedAt 内');
    if (raw.notes != null && (!Array.isArray(raw.notes) || raw.notes.length > 100)) fail('notes 最多 100 项');
    if (source.limitations != null && (!Array.isArray(source.limitations) || source.limitations.length > 100)) fail('source.limitations 最多 100 项');
    return {
      schema, capturedAt, startedAt, endedAt,
      source: { tool: text(source.tool, 'source.tool', true), version: text(source.version, 'source.version'), scope: text(source.scope, 'source.scope'), synthetic: source.synthetic, limitations: (source.limitations || []).map(note => text(note, 'source limitation')) },
      snapshots, notes: (raw.notes || []).map(note => text(note, 'note')), observations
    };
  }
  function windowFeatures(window, foregroundHwnd) {
    const result = [];
    if (window.hwnd === foregroundHwnd) result.push('前台窗口');
    if (window.isTopmost === true) result.push('置顶');
    if (window.isToolWindow === true) result.push('工具窗口');
    if (window.isNoActivate === true) result.push('不激活');
    if (window.isTransparent === true) result.push('透明扩展样式');
    if (window.isLayered === true) result.push('分层窗口');
    if (window.isCloaked === true) result.push('DWM cloaked');
    if (window.isVisible === false) result.push('当前不可见');
    if (window.displayAffinity != null && window.displayAffinity !== 0) result.push('显示亲和性=' + window.displayAffinity);
    if (!window.title) result.push('标题空 / 未采集');
    return result;
  }
  function correlateCase(state, events, audit, now = new Date().toISOString(), evicted = 0) {
    const start = Date.parse(state.lastStartedAt), end = Date.parse(state.finishedAt || now);
    const hasInterval = Number.isFinite(start) && Number.isFinite(end) && end >= start;
    // Control clicks and markers are not evidence that the tested native action reached the page.
    const browser = hasInterval ? events.filter(event => {
      const time = Date.parse(event.timestamp);
      return time >= start && time <= end && event.category !== 'marker' &&
        (['window', 'document'].includes(event.target) || /#(?:test-input|test-autocomplete|test-textarea|click-target|selection-target|drop-target)(?:\b|$)/.test(event.target || ''));
    }) : [];
    const samples = hasInterval && audit ? audit.snapshots.filter(item => Date.parse(item.timestamp) >= start && Date.parse(item.timestamp) <= end) : [];
    const reasons = [];
    if (!hasInterval) reasons.push('尚未开始本项');
    if (!state.recordingAtStart) reasons.push('开始时网页未记录');
    if (state.recordingInterrupted) reasons.push('本项记录曾暂停，网页证据不连续');
    if (!state.finishedAt) reasons.push('尚未记录本项结束时间');
    if (!state.nativeActionConfirmed) reasons.push('未确认原生动作实际发生');
    if (!state.note?.trim()) reasons.push('缺少人工证据备注');
    if (!browser.length) reasons.push('区间内保留的靶场 / 页面事件为零，不能判定通过');
    else if (!browser.some(event => event.isTrusted === true)) reasons.push('没有可信网页事件，合成事件不能验证真实交互');
    if (evicted) reasons.push('旧网页日志已被覆盖，区间可能不完整');
    if (!audit) reasons.push('未导入本机窗口审计');
    else {
      if (audit.source.synthetic) reasons.push('导入的是合成宿主 / 示例，不能代表生产版本运行');
      if (!samples.length) reasons.push('本项时间段没有本机窗口快照');
    }
    return { status: reasons.length ? 'insufficient' : 'review-required', browserRecords: browser.length, trustedBrowserRecords: browser.filter(event => event.isTrusted === true).length, syntheticBrowserRecords: browser.filter(event => event.isTrusted === false).length, auditSamples: samples.length, reasons, interpretation: '只按同机时间区间并列证据，不建立因果关系；此汇总不会自动判定通过。' };
  }
  const api = Object.freeze({ LIMITS, parseAudit, windowFeatures, correlateCase });
  if (typeof module === 'object' && module.exports) module.exports = api;
  else Object.defineProperty(root, 'InteractionProbeAudit', { value: api });
})(globalThis);
