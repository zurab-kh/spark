// Run: node --test tests/browser-extension
import test from 'node:test';
import assert from 'node:assert/strict';
import { shouldIntercept, baseName, isTorrent, fileNameFor, youtubeVideoId } from '../../browser-extension/filter.js';

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

test('.torrent downloads are intercepted regardless of size', () => {
  const small = { totalBytes: 30 * 1024 };
  assert.equal(shouldIntercept(fresh({ ...small, url: 'https://tracker.example/files/linux.torrent' }), now), true);
  assert.equal(shouldIntercept(fresh({ ...small, url: 'https://t.example/dl.php?id=7', mime: 'application/x-bittorrent' }), now), true);
  assert.equal(shouldIntercept(fresh({ ...small, url: 'https://t.example/dl.php?id=7', filename: 'C:\\Users\\u\\Downloads\\Movie.torrent' }), now), true);
  assert.equal(shouldIntercept(fresh({ ...small, url: 'https://t.example/dl.php?id=7', finalUrl: 'https://cdn.example/x/Movie.TORRENT' }), now), true);
  // Still a small ordinary file → browser.
  assert.equal(shouldIntercept(fresh({ ...small, url: 'https://t.example/readme.txt' }), now), false);
});

test('.torrent rule keeps the freshness and protocol checks', () => {
  const torrent = { url: 'https://t.example/a.torrent', totalBytes: 1000 };
  assert.equal(shouldIntercept(fresh({ ...torrent, state: 'complete' }), now), false);
  assert.equal(shouldIntercept(fresh({ ...torrent, startTime: '2026-10-01T10:00:00Z' }), now), false);
  assert.equal(shouldIntercept(fresh({ url: 'data:application/x-bittorrent,xx', mime: 'application/x-bittorrent' }), now), false);
});

test('isTorrent', () => {
  assert.equal(isTorrent({ url: 'https://a/b.torrent' }), true);
  assert.equal(isTorrent({ url: 'https://a/b.torrent?x=1' }), true);
  assert.equal(isTorrent({ url: 'https://a/b', mime: 'application/x-bittorrent; charset=binary' }), true);
  assert.equal(isTorrent({ url: 'https://a/b.torrent.zip' }), false);
  assert.equal(isTorrent({ url: 'https://a/b.zip', mime: 'application/zip' }), false);
  assert.equal(isTorrent(null), false);
});

test('fileNameFor gives torrents found by MIME a .torrent name', () => {
  assert.equal(fileNameFor({ url: 'https://a/big.zip', filename: 'C:\\d\\big.zip' }), 'big.zip');
  assert.equal(fileNameFor({ url: 'https://a/x.torrent', filename: '' }), 'x.torrent');
  assert.equal(fileNameFor({ url: 'https://a/dl.php?id=1', mime: 'application/x-bittorrent', filename: '' }), 'download.torrent');
  assert.equal(fileNameFor({ url: 'https://a/dl.php?id=1', mime: 'application/x-bittorrent', filename: 'C:\\d\\Movie.torrent' }), 'Movie.torrent');
  assert.equal(fileNameFor({ url: 'https://a/', mime: 'application/x-bittorrent' }), 'download.torrent');
});

test('YouTube video pages are recognised', () => {
  assert.equal(youtubeVideoId('https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&t=42'), 'dQw4w9WgXcQ');
  assert.equal(youtubeVideoId('https://youtu.be/dQw4w9WgXcQ?si=abc'), 'dQw4w9WgXcQ');
  assert.equal(youtubeVideoId('https://m.youtube.com/shorts/aqz-KE-bpKQ'), 'aqz-KE-bpKQ');
  assert.equal(youtubeVideoId('https://music.youtube.com/watch?v=jNQXAC9IVRw'), 'jNQXAC9IVRw');
});

test('other pages are not videos', () => {
  assert.equal(youtubeVideoId('https://www.youtube.com/'), null);
  assert.equal(youtubeVideoId('https://www.youtube.com/@channel/videos'), null);
  assert.equal(youtubeVideoId('https://www.youtube.com/watch?v=short'), null);
  assert.equal(youtubeVideoId('https://notyoutube.com/watch?v=dQw4w9WgXcQ'), null);
  assert.equal(youtubeVideoId('chrome://extensions'), null);
  assert.equal(youtubeVideoId(undefined), null);
});
