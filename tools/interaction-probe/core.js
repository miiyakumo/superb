(function (root) {
  'use strict';
  class RingBuffer {
    constructor(capacity = 5000) {
      if (!Number.isInteger(capacity) || capacity < 1) throw new RangeError('capacity must be positive');
      this.capacity = capacity; this.clear();
    }
    clear() { this.buffer = new Array(this.capacity); this.start = 0; this.size = 0; this.evicted = 0; }
    push(value) {
      if (this.size < this.capacity) { this.buffer[(this.start + this.size) % this.capacity] = structuredClone(value); this.size++; }
      else { this.buffer[this.start] = structuredClone(value); this.start = (this.start + 1) % this.capacity; this.evicted++; }
    }
    snapshot() { return structuredClone(Array.from({ length: this.size }, (_, i) => this.buffer[(this.start + i) % this.capacity])); }
  }
  class EventCounter {
    constructor() { this.clear(); }
    clear() { this.observed = 0; this.byPhase = Object.create(null); this.seen = new WeakSet(); this.recording = false; }
    observe(event, phase) {
      if (!this.recording) return false;
      if (!this.seen.has(event)) { this.seen.add(event); this.observed++; }
      const key = event.type + ':' + phase;
      this.byPhase[key] = (this.byPhase[key] || 0) + 1;
      return true;
    }
  }
  class KeyCycles {
    constructor(capacity = 64) { this.held = new Map(); this.capacity = capacity; }
    clear() { this.held.clear(); }
    accept(event, now) {
      const key = boundedKey(event.key), code = boundedKey(event.code);
      const location = Number.isInteger(event.location) ? event.location : 0;
      const id = `${event.isTrusted ? 'trusted' : 'synthetic'}:${code || key}:${location}`;
      const previous = this.held.get(id);
      if (event.type === 'keydown') {
        if (previous && event.repeat) { previous.repeats++; return { kind: 'repeat' }; }
        if (previous) return { kind: 'duplicate-down' };
        if (this.held.size >= this.capacity) this.held.delete(this.held.keys().next().value);
        this.held.set(id, { key, code, location, isTrusted: Boolean(event.isTrusted), downAt: now, repeats: 0 });
        return { kind: 'down' };
      }
      if (event.type !== 'keyup') return { kind: 'ignored' };
      if (!previous) return { kind: 'orphan-up' };
      this.held.delete(id);
      return { kind: 'paired', durationMs: Math.round(now - previous.downAt), repeats: previous.repeats };
    }
  }
  function clipboardMetadata(event, selectedLength = 0) {
    const types = boundedTypes(event.clipboardData?.types);
    let textLength = null;
    try { if (event.type === 'paste' && types.includes('text/plain')) textLength = event.clipboardData.getData('text/plain').length; } catch { /* Browser-provided metadata may be unavailable. */ }
    return { types, textLength, selectedLength: event.type === 'paste' ? null : selectedLength, fileCount: event.clipboardData?.files?.length || 0, contentsStored: false };
  }
  function boundedTypes(types) { return Array.from(types || []).slice(0, 32).map(type => String(type).slice(0, 80)); }
  function boundedKey(value) { return String(value || '').slice(0, 80); }
  const api = Object.freeze({ RingBuffer, EventCounter, KeyCycles, clipboardMetadata, boundedTypes, boundedKey });
  if (typeof module === 'object' && module.exports) module.exports = api;
  else Object.defineProperty(root, 'InteractionProbeCore', { value: api });
})(globalThis);
