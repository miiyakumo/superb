'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { RingBuffer, EventCounter, KeyCycles, clipboardMetadata, boundedTypes } = require('./core.js');

test('ring stays bounded and preserves newest records in order', () => {
  const ring = new RingBuffer(3);
  for (let id = 0; id < 10000; id++) ring.push({ id });
  assert.equal(ring.size, 3); assert.equal(ring.evicted, 9997);
  assert.deepEqual(ring.snapshot().map(item => item.id), [9997, 9998, 9999]);
});
test('ring snapshots and incoming nested objects do not mutate retained evidence', () => {
  const ring = new RingBuffer(3), input = { details: { label: 'original' } };
  ring.push(input); input.details.label = 'changed';
  const snapshot = ring.snapshot(); snapshot[0].details.label = 'outside';
  assert.equal(ring.snapshot()[0].details.label, 'original');
});
test('clear resets retained evidence and eviction counters', () => {
  const ring = new RingBuffer(1); ring.push({ n: 1 }); ring.push({ n: 2 }); ring.clear();
  assert.equal(ring.size, 0); assert.equal(ring.evicted, 0); assert.deepEqual(ring.snapshot(), []);
});
test('capture and bubble count once as a distinct DOM Event but have separate phase counts', () => {
  const counter = new EventCounter(), event = { type: 'keydown' }; counter.recording = true;
  counter.observe(event, 'capture'); counter.observe(event, 'bubble');
  assert.equal(counter.observed, 1); assert.equal(counter.byPhase['keydown:capture'], 1); assert.equal(counter.byPhase['keydown:bubble'], 1);
  counter.observe({ type: 'keydown' }, 'capture'); assert.equal(counter.observed, 2);
});
test('paused counters reject raw observation and clear resets recording and counts', () => {
  const counter = new EventCounter();
  assert.equal(counter.observe({ type: 'keydown' }, 'capture'), false); assert.equal(counter.observed, 0);
  counter.recording = true; counter.observe({ type: 'keydown' }, 'capture'); counter.clear();
  assert.equal(counter.recording, false); assert.equal(counter.observed, 0); assert.deepEqual(Object.keys(counter.byPhase), []);
});
const key = (type, extra = {}) => ({ type, key: 'a', code: 'KeyA', location: 0, isTrusted: true, repeat: false, ...extra });
test('key cycles pair one observed down/up with repeats and do not mix synthetic input', () => {
  const cycles = new KeyCycles();
  assert.equal(cycles.accept(key('keydown'), 10).kind, 'down');
  assert.equal(cycles.accept(key('keydown', { repeat: true }), 30).kind, 'repeat');
  assert.equal(cycles.accept(key('keyup', { isTrusted: false }), 45).kind, 'orphan-up');
  assert.deepEqual(cycles.accept(key('keyup'), 60), { kind: 'paired', durationMs: 50, repeats: 1 });
  assert.equal(cycles.held.size, 0);
});
test('duplicate downs keep earliest time and clearing removes stale held keys', () => {
  const cycles = new KeyCycles(); cycles.accept(key('keydown'), 1);
  assert.equal(cycles.accept(key('keydown'), 20).kind, 'duplicate-down');
  assert.equal(cycles.accept(key('keyup'), 31).durationMs, 30);
  cycles.accept(key('keydown'), 40); cycles.clear(); assert.equal(cycles.accept(key('keyup'), 50).kind, 'orphan-up');
});
test('adversarial synthetic key identities and strings stay bounded', () => {
  const cycles = new KeyCycles(4);
  for (let i = 0; i < 1000; i++) cycles.accept(key('keydown', { code: String(i) + 'x'.repeat(1000), key: 'k'.repeat(1000) }), i);
  assert.equal(cycles.held.size, 4);
  for (const value of cycles.held.values()) { assert.ok(value.code.length <= 80); assert.ok(value.key.length <= 80); }
});
test('paste metadata includes only types and lengths, never clipboard body', () => {
  const secret = 'DO_NOT_KEEP_PASTE_CONTENT';
  const result = clipboardMetadata({ type: 'paste', clipboardData: { types: ['text/plain', 'text/html'], files: [], getData: () => secret } });
  assert.equal(result.textLength, secret.length); assert.equal(result.contentsStored, false);
  assert.ok(!JSON.stringify(result).includes(secret)); assert.deepEqual(result.types, ['text/plain', 'text/html']);
});
test('copy/cut do not read clipboard and unavailable paste data is safe', () => {
  let reads = 0;
  const clipboardData = { types: ['text/plain'], files: [], getData: () => { reads++; throw Error('not available'); } };
  assert.equal(clipboardMetadata({ type: 'copy', clipboardData }, 8).selectedLength, 8);
  clipboardMetadata({ type: 'cut', clipboardData }, 6); assert.equal(reads, 0);
  assert.equal(clipboardMetadata({ type: 'paste', clipboardData }).textLength, null); assert.equal(reads, 1);
});
test('arbitrary MIME type lists stay bounded', () => {
  const types = boundedTypes(Array.from({ length: 100 }, () => 'a'.repeat(1000)));
  assert.equal(types.length, 32); assert.ok(types.every(type => type.length === 80));
});
test('page source has no network, persistent storage, clipboard polling or raw input serialization', () => {
  const source = readFileSync(join(__dirname, 'probe.js'), 'utf8');
  assert.doesNotMatch(source, /\b(?:fetch|XMLHttpRequest|WebSocket|EventSource)\s*\(/);
  assert.doesNotMatch(source, /navigator\.(?:clipboard|sendBeacon)|\b(?:localStorage|sessionStorage|indexedDB)\b|document\.cookie/);
  assert.doesNotMatch(source, /(?:data|value)\s*:\s*event\.(?:data|target\.value)\s*[,}]/);
  assert.match(source, /dataLength:[^\n]*event\.data\.length/);
  assert.match(source, /valueLength:[^\n]*event\.target\.value\.length/);
  assert.match(source, /isTrusted: event \? Boolean\(event\.isTrusted\) : null/);
});
