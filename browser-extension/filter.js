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

  const size = item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : -1;
  if (size >= 0 && size < MIN_SIZE) return false;
  return true;
}

// item.filename is a full local path ("C:\Users\…\file.zip" on Windows) — keep only the file name.
export function baseName(path) {
  if (!path) return null;
  const name = path.split(/[\\/]/).pop();
  return name || null;
}
