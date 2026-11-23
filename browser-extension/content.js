// Alt+клик по ссылке = «скачай средствами браузера»: сообщаем service worker'у, чтобы он не перехватывал.
(() => {
  const notify = (event) => {
    if (!event.altKey) return;
    try {
      chrome.runtime.sendMessage({ type: 'alt-click', at: Date.now() }).catch(() => {});
    } catch {
      // расширение перезагружено — вкладка осталась со старым скриптом
    }
  };
  addEventListener('mousedown', notify, true);
  addEventListener('click', notify, true);
  addEventListener('auxclick', notify, true);
})();
