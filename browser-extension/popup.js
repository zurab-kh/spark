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

function ping() {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST, { type: 'ping' }, (reply) => {
      const err = chrome.runtime.lastError;
      if (err) reject(new Error(err.message));
      else resolve(reply);
    });
  });
}

async function checkConnection() {
  try {
    const reply = await ping();
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

(async () => {
  const { enabled = true } = await chrome.storage.local.get('enabled');
  toggle.checked = enabled;
  toggle.addEventListener('change', () => chrome.storage.local.set({ enabled: toggle.checked }));
  await checkConnection();
})();
