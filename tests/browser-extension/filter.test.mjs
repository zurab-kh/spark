// Run: node --test tests/browser-extension
import test from 'node:test';
import assert from 'node:assert/strict';
import { shouldIntercept, baseName } from '../../browser-extension/filter.js';

const now = Date.parse('2026-11-23T19:00:00Z');
const fresh = (over = {}) => ({
  url: 'https://example.com/big.zip',
  state: 'in_progress',
  startTime: new Date(now - 500).toISOString(),
  totalBytes: 50 * 1024 * 1024,
  ...over,
});

test('new in-progress download is intercepted', () => {
  assert.equal(shouldIntercept(fresh(), now), true);
});

test('history items reported on extension start are ignored', () => {
  // What Chrome replays from its history: completed, started long ago.
  assert.equal(shouldIntercept(fresh({ state: 'complete', startTime: '2026-10-01T10:00:00Z' }), now), false);
  assert.equal(shouldIntercept(fresh({ state: 'interrupted' }), now), false);
  // Even an in-progress item restored from history is not new if it started minutes ago.
  assert.equal(shouldIntercept(fresh({ startTime: new Date(now - 5 * 60_000).toISOString() }), now), false);
});

test('missing or bad start time is not intercepted', () => {
  assert.equal(shouldIntercept(fresh({ startTime: undefined }), now), false);
  assert.equal(shouldIntercept(fresh({ startTime: 'garbage' }), now), false);
});

test('small, non-http and unknown-state items are skipped', () => {
  assert.equal(shouldIntercept(fresh({ totalBytes: 200 * 1024 }), now), false);
  assert.equal(shouldIntercept(fresh({ url: 'blob:https://example.com/x' }), now), false);
  assert.equal(shouldIntercept(fresh({ url: 'data:text/plain,hi' }), now), false);
  assert.equal(shouldIntercept(fresh({ state: undefined }), now), false);
});

test('unknown size is intercepted', () => {
  assert.equal(shouldIntercept(fresh({ totalBytes: 0, fileSize: 0 }), now), true);
});

test('baseName handles Windows and URL-style paths', () => {
  assert.equal(baseName('C:\\Users\\Zu\\Downloads\\Grok_Bot_0.44.0_Setup.exe'), 'Grok_Bot_0.44.0_Setup.exe');
  assert.equal(baseName('/home/u/file.iso'), 'file.iso');
  assert.equal(baseName(''), null);
});
