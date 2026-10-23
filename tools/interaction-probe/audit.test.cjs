'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { LIMITS, parseAudit, windowFeatures, correlateCase } = require('./audit.js');
const timestamp = '2026-10-22T10:00:00.000Z';
const window = { hwnd: '0x1234', processId: 100, processName: 'SyntheticHost', title: 'Synthetic window', className: 'TestClass', isTarget: true, isVisible: true, isTopmost: true, isToolWindow: true, isNoActivate: true, isTransparent: false, isLayered: true, isCloaked: false, ownerHwnd: '0x0', displayAffinity: null, affinityError: 5, style: '0x80000000', exStyle: 136, rect: { left: 0, top: 0, right: 100, bottom: 50 } };
const fixture = () => ({ schema: 'superb-window-audit/v1', capturedAt: timestamp, startedAt: timestamp, endedAt: timestamp, source: { tool: 'synthetic-test', version: '1', scope: 'target PID', synthetic: true, limitations: ['Synthetic only'] }, snapshots: [{ timestamp, foregroundHwnd: '0x1234', windows: [structuredClone(window)] }], notes: ['Synthetic test data'] });
const parse = value => parseAudit(JSON.stringify(value));

test('valid audit keeps null/unreadable separate from zero affinity and retains target metadata', () => {
  const result = parse(fixture()), actual = result.snapshots[0].windows[0];
  assert.equal(result.observations, 1); assert.equal(actual.displayAffinity, null); assert.equal(actual.affinityError, 5);
  assert.equal(actual.isTarget, true); assert.equal(actual.isLayered, true); assert.equal(actual.exStyle, 136);
  assert.equal(result.source.synthetic, true); assert.deepEqual(result.source.limitations, ['Synthetic only']);
});
test('imported strings are inert data and unknown properties are discarded without prototype mutation', () => {
  const data = fixture(); data.snapshots[0].windows[0].title = '<img src=x onerror=alert(1)>';
  const input = JSON.stringify(data).replace('"notes":', '"__proto__":{"polluted":true},"notes":');
  const result = parseAudit(input); assert.equal(result.snapshots[0].windows[0].title, data.snapshots[0].windows[0].title);
  assert.equal({}.polluted, undefined); assert.equal(Object.hasOwn(result, '__proto__'), false);
  const source = readFileSync(join(__dirname, 'probe.js'), 'utf8');
  assert.doesNotMatch(source, /\.innerHTML\s*=|insertAdjacentHTML|\beval\s*\(|new\s+Function\s*\(/);
  assert.match(source, /cell\.textContent = value/);
});
test('malformed schema, timestamps, missing synthetic flag and string booleans are rejected', () => {
  for (const mutate of [data => { data.schema = 'other/v1'; }, data => { data.snapshots[0].timestamp = '2026-10-22 10:00:00'; }, data => { delete data.source.synthetic; }, data => { data.snapshots[0].windows[0].isVisible = 'false'; }, data => { data.startedAt = '2026-10-23T00:00:00Z'; }]) {
    const data = fixture(); mutate(data); assert.throws(() => parse(data), TypeError);
  }
  assert.throws(() => parseAudit('<html>no</html>'), /无法解析/);
});
test('file size, sample count, observation count and strings have independent bounds', () => {
  assert.throws(() => parseAudit('x'.repeat(LIMITS.bytes + 1)), /4 MiB/);
  const samples = fixture(); samples.snapshots = Array(501).fill(samples.snapshots[0]); assert.throws(() => parse(samples), /500/);
  const windows = fixture(); windows.snapshots[0].windows = Array(20001).fill({ hwnd: 'x' }); assert.throws(() => parse(windows), /20,000/);
  const text = fixture(); text.snapshots[0].windows[0].title = 'x'.repeat(513); assert.throws(() => parse(text), /512/);
});
test('snapshot timestamps normalize to UTC and must fall inside declared collection period', () => {
  const data = fixture(); data.snapshots[0].timestamp = '2026-10-22T18:00:00.0000000+08:00';
  assert.equal(parse(data).snapshots[0].timestamp, timestamp);
  data.snapshots[0].timestamp = '2026-10-22T18:00:01+08:00'; assert.throws(() => parse(data), /快照时间/);
});
test('empty valid audit never fabricates window evidence; style features are neutral descriptions', () => {
  const data = fixture(); data.snapshots = []; const audit = parse(data);
  assert.equal(audit.observations, 0); assert.deepEqual(audit.snapshots, []);
  const features = windowFeatures(window, '0x1234'); assert.ok(features.includes('前台窗口')); assert.ok(features.includes('置顶'));
  assert.ok(!features.some(value => /恶意|通过|安全|不可检测/.test(value)));
});
const state = () => ({ lastStartedAt: timestamp, finishedAt: '2026-10-22T10:00:01.000Z', recordingAtStart: true, nativeActionConfirmed: true, note: 'Observed synthetic action', status: 'checked' });
const browser = () => [{ timestamp, target: 'input#test-input', category: 'keyboard', isTrusted: true }];
test('zero browser events and manual checked status never produce pass', () => {
  const review = correlateCase(state(), [], parse(fixture()));
  assert.equal(review.status, 'insufficient'); assert.equal(review.browserRecords, 0);
  assert.ok(review.reasons.some(value => /为零/.test(value))); assert.ok(review.reasons.some(value => /合成宿主/.test(value)));
});
test('control events and markers are excluded; timestamps are bounded to the case', () => {
  const events = [...browser(), { ...browser()[0], target: 'button#case-A-start' }, { ...browser()[0], category: 'marker' }, { ...browser()[0], timestamp: '2026-10-22T10:00:02Z' }];
  assert.equal(correlateCase(state(), events, parse(fixture())).browserRecords, 1);
});
test('complete sources still require manual review and never establish causation', () => {
  const audit = fixture(); audit.source.synthetic = false;
  const review = correlateCase(state(), browser(), parse(audit));
  assert.equal(review.status, 'review-required'); assert.equal(review.auditSamples, 1); assert.equal(review.trustedBrowserRecords, 1);
  assert.match(review.interpretation, /不建立因果/); assert.deepEqual(review.reasons, []);
});
test('missing native confirmation, paused start, unfinished run and eviction explicitly weaken evidence', () => {
  const incomplete = { ...state(), recordingAtStart: false, nativeActionConfirmed: false, note: '', finishedAt: null };
  const review = correlateCase(incomplete, browser(), null, '2026-10-22T10:00:02Z', 1);
  assert.equal(review.status, 'insufficient');
  for (const pattern of [/未记录/, /结束时间/, /未确认/, /备注/, /覆盖/, /未导入/]) assert.ok(review.reasons.some(value => pattern.test(value)));
});
test('synthetic-only browser input and paused intervals remain incomplete evidence', () => {
  const audit = fixture(); audit.source.synthetic = false;
  const review = correlateCase({ ...state(), recordingInterrupted: true }, [{ ...browser()[0], isTrusted: false }], parse(audit));
  assert.equal(review.status, 'insufficient'); assert.ok(review.reasons.some(value => /曾暂停/.test(value))); assert.ok(review.reasons.some(value => /没有可信/.test(value)));
});
test('audit and browser scripts do not open network or persistence paths', () => {
  for (const file of ['audit.js', 'probe.js']) {
    const source = readFileSync(join(__dirname, file), 'utf8');
    assert.doesNotMatch(source, /\b(?:fetch|XMLHttpRequest|WebSocket|EventSource)\s*\(|navigator\.(?:clipboard|sendBeacon)|\b(?:localStorage|sessionStorage|indexedDB)\b|document\.cookie/);
  }
});
