// Spark: перехват загрузок браузера. Отменяем загрузку в браузере только после того,
// как Spark подтвердил приём ссылки (ok:true); иначе браузер качает сам.
import { shouldIntercept, baseName } from './filter.js';

const HOST = 'com.spark.downloader';
const ALT_WINDOW_MS = 3000;       // сколько после Alt+клика загрузка считается «браузерной»

let lastAltAt = 0;

const altRecent = () => Date.now() - lastAltAt < ALT_WINDOW_MS;

async function isEnabled() {
  const { enabled = true } = await chrome.storage.local.get('enabled');
  return enabled;
}

function sendToHost(message) {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST, message, (reply) => {
      const err = chrome.runtime.lastError;
      if (err) reject(new Error(err.message));
      else resolve(reply);
    });
  });
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

async function refreshBadge() {
  const on = await isEnabled();
  await chrome.action.setBadgeText({ text: on ? '' : 'off' });
  await chrome.action.setBadgeBackgroundColor({ color: '#8a8a8a' });
}

chrome.downloads.onCreated.addListener(async (item) => {
  try {
    // shouldIntercept also filters out the download history Chrome replays when the extension starts.
    if (!shouldIntercept(item) || !(await isEnabled()) || altRecent()) return;

    const message = {
      v: 2,                       // protocol version: Spark refuses messages from older extension builds
      startTime: item.startTime,  // Spark double-checks that this is a new download, not browser history
      url: item.url,
      referer: item.referrer || null,
      fileName: baseName(item.filename),
      cookies: await cookieHeader(item.url),
    };
    if (altRecent()) return;   // Alt-клик мог дойти, пока собирали cookies

    let reply;
    try {
      reply = await sendToHost(message);
    } catch (e) {
      console.warn('Spark native host unavailable:', e.message);
      return;                  // хоста нет — браузер качает сам
    }
    if (!reply || reply.ok !== true) return;

    const [current] = await chrome.downloads.search({ id: item.id });
    if (current && current.state === 'complete') return;   // успел скачаться — не трогаем
    await chrome.downloads.cancel(item.id);
    await chrome.downloads.erase({ id: item.id });
  } catch (e) {
    console.warn('Spark interception failed:', e);
  }
});

chrome.runtime.onMessage.addListener((message, sender) => {
  if (message && message.type === 'alt-click' && sender.tab) lastAltAt = Date.now();
});

chrome.runtime.onInstalled.addListener(refreshBadge);
chrome.runtime.onStartup.addListener(refreshBadge);
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.enabled) refreshBadge();
});
