# Spark: перехват загрузок (Chrome, Edge)

Расширение Manifest V3. Передаёт загрузки браузера в Spark через native messaging (`Spark.exe --native-host`).

**ID расширения:** `meffaihaokjhicodaannmgedjflgbcgd` (фиксируется полем `key` в `manifest.json`;
в Spark он зашит в `Strela.App/Services/NativeHostRegistration.cs`).

## Установка
1. Запустите Spark хотя бы раз (он регистрирует native host для Chrome и Edge).
2. Откройте `chrome://extensions` (или `edge://extensions`), включите «Режим разработчика».
3. «Загрузить распакованное» → выберите папку `browser-extension` рядом со `Spark.exe`.

## Как это работает
- Загрузка ≥ 1 МБ (или неизвестного размера) по http/https уходит в Spark вместе с Referer и cookies;
  в браузере она отменяется только после ответа `ok:true`. Нет Spark или он выключил перехват — браузер качает сам.
- Alt + клик по ссылке — скачать средствами браузера.

## Ключ
Публичный ключ — в `manifest.json`. Приватный ключ (нужен только для упаковки .crx) в репозиторий не входит.
ID = первые 16 байт SHA-256 от DER публичного ключа, цифры 0–f → буквы a–p.
