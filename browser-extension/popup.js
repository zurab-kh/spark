import { youtubeVideoId } from './filter.js';

const HOST = 'com.spark.downloader';
const toggle = document.getElementById('enabled');
const status = document.getElementById('status');
const statusText = document.getElementById('status-text');
const hint = document.getElementById('hint');

function setStatus(state, text, hintText) {
  status.dataset.state = state;
  statusText.textContent = text;
  hint.hidden = !hintText;
  hint.textContent = hintText || '';
}

function send(message) {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST, message, (reply) => {
      const err = chrome.runtime.lastError;
      if (err) reject(new Error(err.message));
      else resolve(reply);
    });
  });
}

async function checkConnection() {
  try {
    const reply = await send({ type: 'ping' });
    if (!reply || reply.ok !== true) {
      setStatus('bad', 'Spark не отвечает');
    } else if (reply.enabled === false) {
      setStatus('bad', 'Связь со Spark есть', 'В настройках Spark выключено «Перехватывать загрузки браузера».');
    } else {
      setStatus('ok', 'Связь со Spark есть');
    }
  } catch {
    setStatus('bad', 'Spark не найден', 'Запустите Spark.exe один раз — он зарегистрирует себя для браузера, затем перезапустите браузер.');
  }
}

// On a YouTube video page: a button that hands the video to Spark (it works even with interception off).
async function setUpVideoButton() {
  const button = document.getElementById('video');
  const result = document.getElementById('video-result');
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const id = tab && youtubeVideoId(tab.url);
  if (!id) return;
  button.hidden = false;
  button.addEventListener('click', async () => {
    button.disabled = true;
    result.hidden = false;
    try {
      const reply = await send({
        v: 2, explicit: true, startTime: new Date().toISOString(),
        url: `https://www.youtube.com/watch?v=${id}`, referer: null, fileName: null, cookies: '',
      });
      result.textContent = reply && reply.ok === true ? 'Видео добавлено в Spark' : `Spark не принял ссылку: ${(reply && reply.error) || 'нет ответа'}`;
    } catch (e) {
      result.textContent = 'Spark не найден: запустите его и попробуйте ещё раз';
      button.disabled = false;
    }
  });
}

(async () => {
  setUpVideoButton();
  const { enabled = true } = await chrome.storage.local.get('enabled');
  toggle.checked = enabled;
  toggle.addEventListener('change', () => chrome.storage.local.set({ enabled: toggle.checked }));
  await checkConnection();
})();
