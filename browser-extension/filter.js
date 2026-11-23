// Spark: which browser downloads to hand over. Kept free of chrome.* so it can be unit-tested in Node.

export const MIN_SIZE = 1024 * 1024;      // under 1 MB — let the browser handle it
export const FRESH_MS = 15000;            // only downloads that started this recently

// chrome.downloads.onCreated also fires for every item in the browser's download history when the
// extension (or the browser) starts. Those are old, usually completed downloads and must never be
// re-downloaded: only a download that is in progress and started moments ago is a real new one.
export function shouldIntercept(item, now = Date.now()) {
  if (!item || item.state !== 'in_progress') return false;
  const started = Date.parse(item.startTime);
  if (!Number.isFinite(started) || now - started > FRESH_MS || started - now > FRESH_MS) return false;

  let url;
  try { url = new URL(item.finalUrl || item.url); } catch { return false; }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return false;

  // .torrent files are tiny, but Spark downloads the torrent itself — always hand them over.
  if (isTorrent(item)) return true;

  const size = item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : -1;
  if (size >= 0 && size < MIN_SIZE) return false;
  return true;
}

export const TORRENT_MIME = 'application/x-bittorrent';

const endsWithTorrent = (s) => typeof s === 'string' && /\.torrent$/i.test(s.trim());

function urlPath(raw) {
  try { return decodeURIComponent(new URL(raw).pathname); } catch { return ''; }
}

// A .torrent download: by file name, by URL path, or by MIME type (download.php?id=… answering application/x-bittorrent).
export function isTorrent(item) {
  if (!item) return false;
  if (typeof item.mime === 'string' && item.mime.toLowerCase().split(';')[0].trim() === TORRENT_MIME) return true;
  return endsWithTorrent(baseName(item.filename)) || endsWithTorrent(urlPath(item.finalUrl)) || endsWithTorrent(urlPath(item.url));
}

// File name to send to Spark. Spark routes a link to its torrent engine when the URL or the name ends with
// .torrent, so a torrent recognised only by its MIME type gets a .torrent name.
export function fileNameFor(item) {
  const name = baseName(item && item.filename);
  if (!isTorrent(item) || endsWithTorrent(name)) return name;
  const fromUrl = baseName(urlPath(item.finalUrl || item.url));
  if (endsWithTorrent(fromUrl)) return fromUrl;
  return (name || 'download') + '.torrent';
}

// item.filename is a full local path ("C:\Users\…\file.zip" on Windows) — keep only the file name.
export function baseName(path) {
  if (!path) return null;
  const name = path.split(/[\\/]/).pop();
  return name || null;
}
