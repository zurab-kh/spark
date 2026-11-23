# «Стрела» — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Менеджер загрузок для Windows на C#/WPF, который качает прямые ссылки быстрее FDM, не подвисает, переживает сбои и не задаёт лишних вопросов.

**Architecture:** Движок `Strela.Engine` (без UI) делает всё сетевое и дисковое: разведку ссылки, многопоточную загрузку с динамическим делением кусков, замену медленных соединений, повторы, сохранение состояния, очередь. WPF-приложение `Strela.App` только опрашивает снимки состояния движка 4 раза в секунду и отправляет команды. Локальный тестовый сервер `Strela.TestServer` воспроизводит плохие серверы для автотестов и бенчмарка.

**Tech Stack:** .NET 10 (LTS), C#, WPF (`ThemeMode.System`), `System.Net.Http.SocketsHttpHandler`, `System.Text.Json`, `RandomAccess`, ASP.NET Core Kestrel (только тестовый сервер), xUnit, CommunityToolkit.Mvvm, WinForms `NotifyIcon` (трей).

**Spec:** `docs/superpowers/specs/2026-11-23-download-manager-design.md`. Исполнители читают spec и план вместе.

## Global Constraints

- Целевая платформа `net10.0` (движок, инструменты, тесты), `net10.0-windows` для приложения; SDK 10.0.x закреплён в `global.json` (`rollForward: latestFeature`).
- Без прав администратора: SDK ставится в `%LOCALAPPDATA%\Microsoft\dotnet`, автозапуск через `HKCU\...\Run`.
- Публикация: self-contained, `win-x64`, `PublishReadyToRun=true`, обычная папка (без single-file).
- Никаких сетевых запросов, кроме самих загрузок: ни телеметрии, ни проверки обновлений.
- Модальное окно ровно одно: подтверждение «Удалить с файлом». Больше нигде.
- Данные программы лежат в `%LOCALAPPDATA%\Strela\` (`settings.json`, `downloads\{id}.json`, `logs\`).
- Временный файл: `<имя>.strela` в целевой папке.
- Значения по умолчанию: 16 соединений на файл (диапазон 1–32), 3 одновременные загрузки, минимальный кусок 1 МБ (1 048 576 байт), деление остатка начиная с 2 МБ, сохранение состояния раз в 2 с, проверка медленных раз в 2 с, порог 25 % медианы, медленным соединение считается после 6 с, ошибка после 2 минут сплошных неудач, UI опрашивает движок раз в 250 мс, скорость считается как скользящее среднее за 3 с.
- HTTP: только HTTP/1.1, `AutomaticDecompression = None`, User-Agent браузера.
- Тексты для пользователя на русском.

## Review Focus

1. **Сервер при разведке ответил 206, а на запрос куска с ненулевого смещения отдаёт 200 и весь файл.** Ожидание: тело не пишется по смещению куска (иначе файл испорчен). Загрузка переходит в однопоточный режим с нуля, итоговый файл верный. Тест — Task 10.
2. **Папки загрузок нет, она недоступна для записи, или на диске нет места.** Ожидание: программа не падает, загрузка встаёт в «Ошибка» или «Пауза» с понятной причиной. Тест — Task 11.
3. **Имена файлов: кириллица в `filename*=UTF-8''…`, зарезервированные имена (`CON`, `NUL.txt`), имя длиннее 200 символов, точки и пробелы в конце, URL без имени (`download.php?id=5`).** Ожидание: всегда допустимое имя Windows, без исключений. Тест — Task 3.
4. **Пользователь сам удалил `.strela`-файл, пока программа была закрыта.** Ожидание: при запуске загрузка начинается заново без ошибки. Тест — Task 11.
5. **Два одновременных файла с одинаковым именем** (разные ссылки, одно имя). Ожидание: два разных файла, `имя.ext` и `имя (1).ext`, временные файлы не пересекаются. Тест — Task 11.

---

## Структура файлов

```
global.json, .gitignore, Strela.sln
src/Strela.Engine/
  Strela.Engine.csproj            (InternalsVisibleTo Strela.Engine.Tests)
  IStrelaLog.cs                   IStrelaLog, NullLog
  EngineOptions.cs
  DownloadRequest.cs
  Http/StrelaHttp.cs              настроенный HttpClient
  Probe/ProbeResult.cs, Probe/Prober.cs
  Probe/FileNameResolver.cs       имя из заголовков/URL, очистка, уникальный путь
  Segments/Segment.cs             живой кусок (позиция, резерв, конец) — потокобезопасный
  Segments/SegmentPlanner.cs      начальная нарезка и деление (work stealing)
  Storage/PartFile.cs             резерв места, позиционная запись, flush, финализация
  Storage/DownloadState.cs        сохраняемое состояние
  Storage/StateStore.cs           атомарное сохранение/загрузка JSON
  Policies/RetryPolicy.cs, Policies/SpeedMeter.cs
  Policies/SlowConnectionMonitor.cs, Policies/HostLimits.cs
  Jobs/DownloadJob.cs             одна загрузка целиком
  Jobs/SegmentWorker.cs           одно соединение
  Jobs/JobOutcome.cs
  Manager/IDownloadManager.cs, Manager/DownloadManager.cs, Manager/DownloadSnapshot.cs
  Integration/FileUrlDetector.cs, Integration/IpcProtocol.cs, Integration/IpcServer.cs, Integration/IpcClient.cs
src/Strela.App/
  App.xaml(.cs), MainWindow.xaml(.cs)
  ViewModels/MainViewModel.cs, ViewModels/DownloadItemViewModel.cs
  Views/SettingsWindow.xaml(.cs), Views/ToastWindow.xaml(.cs)
  Services/AppSettings.cs, Services/SettingsStore.cs, Services/FileLog.cs, Services/Format.cs
  Services/TrayIcon.cs, Services/ClipboardWatcher.cs, Services/Autostart.cs, Services/UiLagMonitor.cs
tests/Strela.Engine.Tests/        (ссылается на Engine и TestServer)
  ListLog.cs, TestData.cs + тесты по задачам
tests/Strela.App.Tests/           (net10.0-windows)
tools/Strela.TestServer/          Exe; класс TestFileServer используется тестами
tools/Strela.Bench/
docs/benchmark.md, publish.ps1
```

---

### Task 1: Инструменты, решение, git

**Files:**
- Create: `global.json`, `.gitignore`, `Strela.sln`, все `.csproj` из структуры, `src/Strela.Engine/IStrelaLog.cs`, `tests/Strela.Engine.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: `public interface IStrelaLog { void Info(string m); void Warn(string m); void Error(string m, Exception? ex = null); }`, `public sealed class NullLog : IStrelaLog` (`NullLog.Instance`).

- [ ] **Step 1: Получить разрешение пользователя и поставить .NET 10 SDK**

Перед скачиванием спросить пользователя: «Скачать .NET 10 SDK (~250 МБ, dot.net, ставится в `%LOCALAPPDATA%\Microsoft\dotnet`, без админа)?». После «да»:
```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
& $env:TEMP\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
```
Добавить `%LOCALAPPDATA%\Microsoft\dotnet` в начало пользовательского PATH.
Проверка: `dotnet --list-sdks` показывает `10.0.x`.

- [ ] **Step 2: Создать решение и проекты**

`git init`; `.gitignore`: `bin/ obj/ publish/ *.user .vs/`. `global.json` — SDK 10.0.100, `rollForward: latestFeature`. Проекты: classlib `Strela.Engine`, wpf `Strela.App` (`UseWPF`, `UseWindowsForms` = true), xunit `Strela.Engine.Tests` и `Strela.App.Tests`, web `Strela.TestServer` (Exe, Sdk.Web), console `Strela.Bench`. Везде `Nullable=enable`, `TreatWarningsAsErrors=true`. Ссылки: App→Engine; Bench→Engine; Engine.Tests→Engine, TestServer; App.Tests→App, Engine.

- [ ] **Step 3: Smoke-тест**

```csharp
[Fact] public void NullLog_DoesNotThrow() { NullLog.Instance.Error("x", new Exception()); }
```

- [ ] **Step 4: Проверить**

Run: `dotnet build` и `dotnet test`
Expected: Build succeeded, 1 passed.

- [ ] **Step 5: Commit** — `chore: scaffold Strela solution`

---

### Task 2: Тестовый сервер

**Files:**
- Create: `tools/Strela.TestServer/TestFileServer.cs`, `ServeOptions.cs`, `Program.cs`
- Create: `tests/Strela.Engine.Tests/TestData.cs`, `TestServerTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record ServeOptions(
    bool SupportsRanges = true,
    long? BytesPerSecondPerConnection = null,
    int SlowConnectionCount = 0,          // первые N соединений к файлу — медленные
    long SlowBytesPerSecond = 50_000,
    long? DropAfterBytes = null,          // соединение рвётся после N байт тела
    int? MaxConcurrent = null,            // сверх лимита: 503 + Retry-After: 1
    string? ContentDisposition = null,
    int? FixedStatus = null,              // всегда этот статус (404, 403…)
    bool IgnoreRangeAfterProbe = false,   // Range bytes=0-0 → 206, остальные Range → 200 всё тело
    bool OmitContentLength = false);      // chunked, без размера

public sealed class TestFileServer : IAsyncDisposable {
    public static Task<TestFileServer> StartAsync();           // 127.0.0.1, случайный порт
    public Uri AddFile(string name, byte[] content, ServeOptions? options = null); // /f/{n}/{name}
    public void ReplaceContent(Uri url, byte[] content);        // новый ETag
    public void SetOptions(Uri url, ServeOptions options);
    public int ConnectionsOpened(Uri url);
    public int MaxConcurrentSeen(Uri url);
}
// tests: TestData.Random(int size, int seed) -> byte[]; TestData.Sha256(string path) / Sha256(byte[]) -> string
```
- Ответы: `ETag` (хеш содержимого), `Last-Modified`, `Accept-Ranges: bytes` (если `SupportsRanges`). Одиночный Range → 206 + `Content-Range`, без Range → 200. Ограничение скорости — пауза по токенам каждые 16 КБ. `Program.cs`: запускает сервер с файлами `test-64mb.bin` (1 МБ/с на соединение) и `test-256mb.bin` (без лимита), печатает URL.

- [ ] **Step 1: Тесты**

```csharp
[Fact] Range_Returns206WithExactBytes  // Range: bytes=10-19 → 206, 10 байт == content[10..20], Content-Range "bytes 10-19/1000"
[Fact] NoRangeSupport_Returns200Full   // SupportsRanges=false + Range → 200, длина == content.Length
[Fact] RateLimit_IsRespected           // 1 МБ, 500 000 B/s → чтение тела ≥ 1.8 с
[Fact] MaxConcurrent_Returns503        // MaxConcurrent=1, два одновременных GET с медленным чтением → второй 503, Retry-After == 1
```
- [ ] **Step 2:** `dotnet test --filter TestServerTests` → FAIL (нет типа).
- [ ] **Step 3:** Реализовать `TestFileServer` (Kestrel, `WebApplication.CreateSlimBuilder`, `UseUrls("http://127.0.0.1:0")`).
- [ ] **Step 4:** `dotnet test --filter TestServerTests` → 4 passed.
- [ ] **Step 5: Commit** — `test: add throttling test file server`

---

### Task 3: Имена файлов

**Files:**
- Create: `src/Strela.Engine/Probe/FileNameResolver.cs`, `tests/Strela.Engine.Tests/FileNameResolverTests.cs`

**Interfaces:**
- Produces:
```csharp
public static class FileNameResolver {
    public static string Resolve(ContentDispositionHeaderValue? cd, Uri finalUri); // filename* → filename → последний сегмент пути (URL-decode) → "download"
    public static string Sanitize(string name);
    public static string ReserveUniquePath(string folder, string fileName, Func<string, bool> isTaken); // полный путь
}
```
Правила `Sanitize`: символы `<>:"/\|?*` и управляющие → `_`; обрезать точки и пробелы в конце; зарезервированные базовые имена (`CON PRN AUX NUL COM1–9 LPT1–9`, без учёта регистра и расширения) → префикс `_`; длина имени ≤ 200 символов, обрезается основа, расширение сохраняется; пустой результат → `download`. `ReserveUniquePath`: путь занят, если `File.Exists(p) || File.Exists(p + ".strela") || isTaken(p)`; кандидаты `имя.ext`, `имя (1).ext`, `имя (2).ext`…

- [ ] **Step 1: Тесты**

```csharp
[Theory]
[InlineData("attachment; filename*=UTF-8''%D0%BE%D1%82%D1%87%D1%91%D1%82.pdf", "http://x/a", "отчёт.pdf")]
[InlineData("attachment; filename=\"setup.exe\"", "http://x/a", "setup.exe")]
[InlineData(null, "http://x/files/my%20file.zip?x=1", "my file.zip")]
[InlineData(null, "http://x/download.php?id=5", "download.php")]
[InlineData(null, "http://x/", "download")]
Resolve_PicksBestName(string? cd, string url, string expected)

[Theory]
[InlineData("a<b>c.txt", "a_b_c.txt")] [InlineData("CON", "_CON")] [InlineData("nul.txt", "_nul.txt")]
[InlineData("name. . ", "name")] [InlineData("", "download")]
Sanitize_ProducesValidWindowsName(string input, string expected)

[Fact] Sanitize_TruncatesLongNameKeepingExtension   // new string('a', 300) + ".iso" → длина 200, оканчивается на ".iso"
[Fact] ReserveUniquePath_SkipsExistingFinalPartAndTaken  // в temp-папке есть "f.bin" и "f (1).bin.strela", isTaken("…f (2).bin") → результат "f (3).bin"
```
- [ ] **Step 2:** Run `dotnet test --filter FileNameResolverTests` → FAIL.
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Run → all passed.
- [ ] **Step 5: Commit** — `feat(engine): resolve and sanitize file names`

---

### Task 4: HTTP-клиент и разведка

**Files:**
- Create: `src/Strela.Engine/DownloadRequest.cs`, `EngineOptions.cs`, `Http/StrelaHttp.cs`, `Probe/ProbeResult.cs`, `Probe/Prober.cs`
- Test: `tests/Strela.Engine.Tests/ProberTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record DownloadRequest(Uri Url, string? Referer = null,
    IReadOnlyDictionary<string, string>? Headers = null, string? Cookies = null, string? FileName = null);

public sealed record EngineOptions {
    public required string DownloadFolder { get; init; }
    public required string StateFolder { get; init; }
    public int ConnectionsPerFile { get; init; } = 16;
    public int MaxActiveDownloads { get; init; } = 3;
    public long MinSegmentBytes { get; init; } = 1 << 20;
    public int BufferBytes { get; init; } = 512 * 1024;
    public TimeSpan StateSaveInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SlowCheckInterval { get; init; } = TimeSpan.FromSeconds(2);
    public double SlowThreshold { get; init; } = 0.25;
    public TimeSpan SlowGrace { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);   // нет байтов → соединение мёртвое
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(2);
}

public static class StrelaHttp {
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    public static HttpClient Create();
    public static HttpRequestMessage NewRequest(DownloadRequest req, Uri url, long? from = null, long? toInclusive = null);
}

public sealed record ProbeResult(Uri FinalUri, long? TotalBytes, bool SupportsRanges,
    string? ETag, DateTimeOffset? LastModified, string FileName, HttpStatusCode Status);

public static class Prober { public static Task<ProbeResult> ProbeAsync(HttpClient http, DownloadRequest req, CancellationToken ct); }
```
`Create()`: `SocketsHttpHandler { MaxConnectionsPerServer = 256, AutomaticDecompression = None, PooledConnectionLifetime = 5 мин, ConnectTimeout = 15 с, AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }`, `DefaultRequestVersion = HttpVersion.Version11`, `DefaultVersionPolicy = RequestVersionOrLower`, `Timeout = InfiniteTimeSpan`. `NewRequest` ставит UA, Referer, Headers, `Cookie`, Range. `ProbeAsync`: `GET Range: bytes=0-0`, `ResponseHeadersRead`, тело не читается. 206 → размер из `Content-Range`, ranges = true. 200 → размер из `Content-Length` (может быть null), ranges = false. 416/405 → повтор через `HEAD`. Статус ≥ 400 возвращается в `Status` без исключения (решение принимает вызывающий). Имя: `req.FileName` (очищенное) или `FileNameResolver.Resolve`.

- [ ] **Step 1: Тесты** (на `TestFileServer`)

```csharp
[Fact] Probe_RangeServer      // 5000 байт → TotalBytes 5000, SupportsRanges true, ETag != null, FileName "a.bin"
[Fact] Probe_NoRangeServer    // SupportsRanges=false → SupportsRanges false, TotalBytes 5000
[Fact] Probe_NoLength         // OmitContentLength + SupportsRanges=false → TotalBytes null
[Fact] Probe_ContentDisposition  // ContentDisposition "attachment; filename=\"real.iso\"" → FileName "real.iso"
[Fact] Probe_404_ReturnsStatus   // FixedStatus 404 → Status == NotFound, без исключения
[Fact] Http_UsesHttp11AndBrowserUa  // сервер записывает Protocol и User-Agent последнего запроса: "HTTP/1.1", StrelaHttp.UserAgent
```
(Для последнего теста добавить в `TestFileServer` свойства `LastProtocol`, `LastUserAgent`.)
- [ ] **Step 2:** Run `--filter ProberTests` → FAIL.
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Run → passed.
- [ ] **Step 5: Commit** — `feat(engine): http client and probing`

---

### Task 5: Куски и деление

**Files:**
- Create: `src/Strela.Engine/Segments/Segment.cs`, `Segments/SegmentPlanner.cs`
- Test: `tests/Strela.Engine.Tests/SegmentPlannerTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class Segment {
    public Segment(long start, long end, long position);   // end exclusive
    public long Start { get; }
    public long End { get; }          // читается под lock
    public long Position { get; }     // подтверждённо записано до (abs)
    public long Reserved { get; }     // выдано работнику до (abs), Position ≤ Reserved ≤ End
    public long Remaining { get; }    // End - Position
    public bool IsDone { get; }       // Position >= End
    public (long Offset, int Count) Reserve(int available); // под lock: Offset = Reserved; Count = min(available, End - Reserved); Reserved += Count (Count 0 = конец)
    public void Commit(int n);         // под lock: Position += n (после записи на диск)
    public void Abandon();            // под lock: Reserved = Position (соединение оборвалось)
    public Segment? SplitOff(long minRemaining);   // см. алгоритм
}
public static class SegmentPlanner {
    public static List<Segment> InitialSplit(long totalBytes, int connections, long minSegmentBytes);
    public static Segment? Steal(IReadOnlyList<Segment> segments, long minSegmentBytes); // нашёл и отрезал
}
```
Алгоритм (тесты его не определяют полностью):
```
SplitOff(minRemaining):  lock(this)
    free = End - Reserved
    if free < 2 * minRemaining: return null
    mid = Reserved + free / 2, выровнять вниз до 4096 (но > Reserved)
    newSeg = new Segment(mid, End, mid); End = mid; return newSeg
Steal: кандидат = кусок с max(End - Reserved) среди незавершённых; return кандидат.SplitOff(minSegmentBytes)
InitialSplit: n = clamp(totalBytes / minSegmentBytes, 1, connections); n равных кусков, последний забирает остаток; totalBytes == 0 → один кусок [0,0)
```
Работник никогда не пишет байты за `End`: резерв берётся до записи, а `SplitOff` режет только за `Reserved`.

- [ ] **Step 1: Тесты**

```csharp
[Fact] InitialSplit_16Connections_32MB  // 16 кусков по 2 МБ, непрерывные, покрывают [0, 32 МБ)
[Fact] InitialSplit_SmallFile           // 1.5 МБ → 1 кусок
[Fact] InitialSplit_LimitedByMinSegment // 4 МБ, 16 соединений → 4 куска
[Fact] SplitOff_HalvesFreeRemainder     // [0, 8 МБ), Reserve(1 МБ) → SplitOff(1 МБ): новый [4.5 МБ, 8 МБ), старый End == 4.5 МБ
[Fact] SplitOff_RefusesBelowTwoMin      // свободно 1.9 МБ → null
[Fact] SplitOff_NeverBelowReserved      // Reserve(весь остаток − 1 МБ) → SplitOff → null
[Fact] Steal_PicksLargestFree           // куски с free 3, 10, 5 МБ → отрезан от второго
[Fact] Concurrent_ReserveAndSplit_CoverExactlyOnce  // 8 потоков резервируют по 64 КБ и коммитят, 2 потока крадут; итог: объединение [Start, Position) всех кусков == [0, total), без пересечений
```
- [ ] **Step 2:** Run `--filter SegmentPlannerTests` → FAIL.
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Run → passed.
- [ ] **Step 5: Commit** — `feat(engine): segments with work-stealing split`

---

### Task 6: Временный файл

**Files:**
- Create: `src/Strela.Engine/Storage/PartFile.cs`; Test: `tests/Strela.Engine.Tests/PartFileTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class PartFile : IDisposable {
    public static PartFile OpenOrCreate(string partPath, long? totalBytes); // FileShare.Read; SetLength(total) если известен и текущая длина отличается
    public string Path { get; }
    public void Write(long offset, ReadOnlySpan<byte> data);  // RandomAccess.Write, потокобезопасно
    public void FlushToDisk();                                 // FileStream.Flush(true)
    public void Truncate();                                    // SetLength(0) (рестарт с нуля)
    public string Complete(string finalPath, Func<string, bool> isTaken); // закрыть, переименовать; занято → FileNameResolver.ReserveUniquePath; вернуть итоговый путь
    public static void Delete(string partPath);
}
```
Ошибки нехватки места (`HResult & 0xFFFF` = 0x70 или 0x27) пробрасываются как `DiskFullException : IOException`.

- [ ] **Step 1: Тесты**

```csharp
[Fact] OpenOrCreate_Preallocates            // total 10 МБ → длина файла 10 МБ
[Fact] ParallelWrites_ProduceExactContent   // 8 потоков пишут свои части случайных 8 МБ → SHA256 совпадает
[Fact] Complete_RenamesAndAvoidsCollision   // "x.bin" уже есть → возвращает "x (1).bin", .strela удалён
[Fact] Reopen_KeepsExistingData             // записать, Dispose, OpenOrCreate тем же размером → данные на месте
```
- [ ] **Step 2–4:** Run `--filter PartFileTests`: FAIL → реализовать → PASS.
- [ ] **Step 5: Commit** — `feat(engine): preallocated part file with positional writes`

---

### Task 7: Состояние на диске

**Files:**
- Create: `src/Strela.Engine/Storage/DownloadState.cs`, `Storage/StateStore.cs`, `Manager/DownloadSnapshot.cs` (там же `enum DownloadStatus`)
- Test: `tests/Strela.Engine.Tests/StateStoreTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum DownloadStatus { Queued, Downloading, Paused, Completed, Error }
public sealed record SegmentState(long Start, long End, long Position);
public sealed class DownloadState {  // get; set; свойства для System.Text.Json
    Guid Id; string OriginalUrl; string CurrentUrl; string? Referer; Dictionary<string,string>? Headers; string? Cookies; string? RequestedFileName;
    string Folder; string? FileName; string? PartPath; string? FinalPath;
    long? TotalBytes; bool SupportsRanges; string? ETag; DateTimeOffset? LastModified;
    List<SegmentState> Segments; DownloadStatus Status; string? Error;
    long QueueOrder; DateTimeOffset AddedAt; DateTimeOffset? CompletedAt;
    DownloadRequest ToRequest();   // CurrentUrl + Referer/Headers/Cookies/RequestedFileName
}
public sealed class StateStore {
    public StateStore(string folder);
    public void Save(DownloadState s);     // {id}.json.tmp → File.Move(overwrite: true)
    public IReadOnlyList<DownloadState> LoadAll(IStrelaLog log); // битый JSON → Warn и пропустить
    public void Delete(Guid id);
}
```
- [ ] **Step 1: Тесты**

```csharp
[Fact] SaveLoad_RoundTrip                  // все поля, 3 сегмента → LoadAll возвращает эквивалентный объект
[Fact] Save_IsAtomic_NoTmpLeft             // после Save в папке нет *.tmp
[Fact] LoadAll_SkipsCorruptFile            // рядом битый "bad.json" → загружены остальные, в ListLog есть Warn
```
- [ ] **Step 2–4:** FAIL → реализовать → PASS (`--filter StateStoreTests`).
- [ ] **Step 5: Commit** — `feat(engine): atomic download state store`

---

### Task 8: Политики (повторы, скорость, медленные, лимиты хоста)

**Files:**
- Create: `src/Strela.Engine/Policies/RetryPolicy.cs`, `SpeedMeter.cs`, `SlowConnectionMonitor.cs`, `HostLimits.cs`
- Test: `tests/Strela.Engine.Tests/PoliciesTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum ErrorKind { Transient, Throttle, Fatal }
public static class RetryPolicy {
    public static TimeSpan Delay(int attempt, TimeSpan? retryAfter); // 1,2,4,8,16,30,30… с; retryAfter важнее, не больше 5 мин
    public static ErrorKind Classify(HttpStatusCode? status, Exception? ex); // 403/404/410 → Fatal; 429/503 → Throttle; прочие 5xx, IOException, HttpRequestException, TimeoutException → Transient
}
public sealed class SpeedMeter { public SpeedMeter(TimeSpan window); public void Sample(long totalBytes, TimeSpan now); public double BytesPerSecond { get; } }
public readonly record struct WorkerSample(int WorkerId, double BytesPerSecond, long RemainingBytes);
public sealed class SlowConnectionMonitor {
    public SlowConnectionMonitor(double threshold, TimeSpan grace, long minRemaining);
    public IReadOnlyList<int> Evaluate(IReadOnlyList<WorkerSample> samples, TimeSpan now); // нужно ≥ 2 образца; медленный = speed < threshold * медиана И remaining ≥ minRemaining непрерывно ≥ grace; возвращённые id сбрасываются
}
public sealed class HostLimits { public int Ceiling(string host, int requested); public void Lower(string host, int workingConnections); } // Lower ставит потолок max(1, working); на время жизни объекта
```
- [ ] **Step 1: Тесты**

```csharp
[Theory] [InlineData(1,1)] [InlineData(3,4)] [InlineData(6,30)] [InlineData(20,30)] Delay_Backoff(int attempt, int seconds)
[Fact] Delay_RetryAfterWins_Capped          // retryAfter 10 с → 10 с; 1 ч → 5 мин
[Theory] Classify_Maps                      // 404→Fatal, 410→Fatal, 403→Fatal, 503→Throttle, 429→Throttle, 500→Transient, (null, IOException)→Transient
[Fact] SpeedMeter_SlidingWindow             // 0 байт @0 с, 3 МБ @3 с → ~1 МБ/с; потом без изменений до 7 с → 0
[Fact] Slow_FlaggedAfterGrace               // 4 работника 1 МБ/с, №2 100 КБ/с: в 5 с — пусто, в 6.1 с — [2]
[Fact] Slow_NotFlaggedWhenLittleLeft        // у медленного осталось 1 МБ при minRemaining 2 МБ → пусто
[Fact] Slow_RecoveryResetsTimer             // медленный 4 с, нормальный 1 с, снова медленный 4 с → пусто
[Fact] HostLimits_LowerThenCeiling          // Lower("h", 4) → Ceiling("h", 16) == 4; другой хост → 16
```
- [ ] **Step 2–4:** FAIL → реализовать → PASS (`--filter PoliciesTests`).
- [ ] **Step 5: Commit** — `feat(engine): retry, speed, slow-connection and host-limit policies`

---

### Task 9: Загрузка одного файла (основной путь)

**Files:**
- Create: `src/Strela.Engine/Jobs/DownloadJob.cs`, `Jobs/SegmentWorker.cs`, `Jobs/JobOutcome.cs`
- Test: `tests/Strela.Engine.Tests/ListLog.cs`, `DownloadJobTests.cs`

**Interfaces:**
- Consumes: Tasks 3–8.
- Produces:
```csharp
public sealed record JobOutcome(DownloadStatus Status, string? Error = null, string? FinalPath = null); // Completed | Paused | Error
internal sealed class DownloadJob {
    public DownloadJob(DownloadState state, EngineOptions options, HttpClient http, HostLimits limits,
                       StateStore store, IStrelaLog log, Func<string, bool> isPathTaken);
    public DownloadState State { get; }
    public Task<JobOutcome> RunAsync(CancellationToken ct);  // отмена = пауза: сохранить состояние, вернуть Paused
    public long DownloadedBytes { get; }                     // сумма (Position − Start), атомарно
    public int ActiveConnections { get; }
    public string? StatusText { get; }                       // напр. "Ожидание сети", "Повтор через 8 с"
}
```
Поведение `RunAsync` (основной путь; устойчивость — Task 10):
1. Разведка (`Prober`). Новая загрузка (`TotalBytes == null && Segments.Count == 0`): имя → `ReserveUniquePath(Folder, name, isPathTaken)`, `PartPath = final + ".strela"`, сегменты из `InitialSplit` (или один `[0, total)` / `[0, long.MaxValue)` в однопоточном режиме).
2. `PartFile.OpenOrCreate`. Работников запустить `min(HostLimits.Ceiling(host, ConnectionsPerFile), сегментов)`.
3. Работник: берёт сегмент → `GET Range: bytes=Position-(End-1)` → читает тело в буфер `BufferBytes` из `ArrayPool` → `(off, n) = seg.Reserve(read)` → `PartFile.Write(off, buffer[..n])` → `seg.Commit(n)`; при `n < read` или `IsDone` закрывает ответ. Затем `SegmentPlanner.Steal`; нет добычи → завершается.
4. Фоновая петля каждые `StateSaveInterval`: снять позиции → `FlushToDisk()` → записать состояние со снятыми позициями (позиции снимаются **до** flush).
5. Все сегменты `IsDone` (и в однопоточном режиме длина = прочитанное) → `Complete(...)` → `FinalPath`, `Status=Completed`, `CompletedAt`.
6. Однопоточный режим (`!SupportsRanges || TotalBytes == null`): один работник без Range, позиция с 0; при любом обрыве — `Truncate()` и с начала.

Строки лога (тесты ищут подстроки): `"split"` при краже, `"replace slow"` при замене (Task 10), `"restart: file changed"` (Task 10), `"single-stream"` при однопоточном режиме.

- [ ] **Step 1: Тесты** (общий хелпер: `RunJob(Uri url, EngineOptions? o = null)` — новый `DownloadState`, `DownloadJob`, `RunAsync`)

```csharp
[Fact] Downloads_ExactContent_16Connections   // 20 МБ случайных → Completed, SHA256 совпадает, файл "a.bin", .strela нет
[Fact] PerConnectionLimit_UsesManyConnections // 32 МБ, 1 МБ/с на соединение → готово < 6 с, ConnectionsOpened ≥ 16
[Fact] NoTail_WorkStealingKicksIn             // 32 МБ, 1 МБ/с, ConnectionsPerFile 4 → готово < 11 с, в логе есть "split"
[Fact] SingleStream_WhenNoRanges              // SupportsRanges=false, 3 МБ → Completed, хеш верный, в логе "single-stream"
[Fact] SingleStream_UnknownLength             // OmitContentLength + SupportsRanges=false → Completed, хеш верный
[Fact] ZeroByteFile                           // пустой файл → Completed, длина 0
[Fact] Cancel_SavesStateNotAheadOfData        // 32 МБ, 1 МБ/с: отмена на 3-й секунде → Paused; для каждого сегмента из сохранённого JSON байты [Start, Position) в .strela == оригиналу
```
- [ ] **Step 2:** Run `--filter DownloadJobTests` → FAIL.
- [ ] **Step 3:** Реализовать `SegmentWorker` и `DownloadJob`.
- [ ] **Step 4:** Run → passed. Каждый тест прогнать 5 раз подряд (`for` в PowerShell) — без флапов.
- [ ] **Step 5: Commit** — `feat(engine): multi-connection download job`

---

### Task 10: Устойчивость загрузки

**Files:**
- Modify: `src/Strela.Engine/Jobs/DownloadJob.cs`, `Jobs/SegmentWorker.cs`
- Test: `tests/Strela.Engine.Tests/DownloadJobResilienceTests.cs`

**Interfaces:**
- Consumes: `RetryPolicy`, `SlowConnectionMonitor`, `HostLimits` (Task 8).
- Produces: те же типы Task 9; новое поведение:
  - Ошибка соединения или сегмента → `seg.Abandon()`, повтор с `RetryPolicy.Delay`; ошибки одного работника не трогают остальных. `StallTimeout` без байтов = обрыв.
  - `Throttle` (429/503) → этот работник выходит; `HostLimits.Lower(host, активные_без_него)`; его сегмент подхватит другой.
  - `Fatal` → вся загрузка `Error`. Тексты: 404/410 → `"Файл не найден на сервере (404)"` / `"(410)"`; 403 при первой разведке → `"Доступ запрещён (403)"`; 403, когда уже есть прогресс → `"Ссылка устарела — обновите ссылку"`.
  - Все работники безуспешны непрерывно `GiveUpAfter` → `Error`, `"Сервер недоступен: <сообщение>"`.
  - Каждые `SlowCheckInterval` → `SlowConnectionMonitor.Evaluate` → для отмеченных: оборвать ответ, `Abandon`, переоткрыть с `Position`; лог `"replace slow"`.
  - Докачка: если ETag (или, при отсутствии ETag, Last-Modified) или размер изменились → лог `"restart: file changed"`, `Truncate`, новые сегменты.
  - Ответ 200 на запрос с ненулевым смещением → ничего не писать, перейти в однопоточный режим с нуля (лог `"single-stream"`).
  - `DiskFullException` → `Paused`, `"Нет места на диске"`. `UnauthorizedAccessException` или `DirectoryNotFoundException` при открытии файла → `Error`, `"Нет доступа к папке: <путь>"`.

- [ ] **Step 1: Тесты**

```csharp
[Fact] DropsMidStream_StillCorrect          // 16 МБ, DropAfterBytes 1 МБ → Completed, хеш верный
[Fact] Server503_LowersConnections          // 16 МБ, MaxConcurrent 4 → Completed, хеш верный, переданный в job HostLimits.Ceiling(host, 16) ≤ 4
[Fact] SlowConnection_Replaced              // 16 МБ, 1 МБ/с, ConnectionsPerFile 4, SlowConnectionCount 1, SlowGrace 2 с, SlowCheckInterval 0.5 с → готово < 12 с, лог содержит "replace slow"
[Fact] NotFound_IsError                     // FixedStatus 404 → Error, "Файл не найден на сервере (404)"
[Fact] Forbidden_AfterProgress_SaysStale    // начать, пауза, SetOptions(FixedStatus 403), продолжить → Error "Ссылка устарела — обновите ссылку"
[Fact] FileChanged_RestartsFromZero         // скачать часть, пауза, ReplaceContent(новые байты), продолжить → Completed, хеш == новое содержимое, лог "restart: file changed"
[Fact] RangeIgnored_FallsBackSafely         // IgnoreRangeAfterProbe, 8 МБ → Completed, хеш верный, лог "single-stream"   (Review Focus 1)
[Fact] ResumeFromOlderStateCopy             // 32 МБ, 1 МБ/с: на 3-й с скопировать JSON состояния, на 5-й отменить; новый DownloadJob из КОПИИ → Completed, хеш верный (имитация падения)
[Fact] GivesUpAfterTimeout                  // сервер остановлен после разведки, GiveUpAfter 3 с → Error, начинается с "Сервер недоступен"
```
- [ ] **Step 2–4:** FAIL → реализовать → PASS (`--filter DownloadJobResilienceTests`), 5 прогонов подряд без флапов.
- [ ] **Step 5: Commit** — `feat(engine): retries, throttling, slow-connection replacement, safe resume`

---

### Task 11: Очередь и менеджер

**Files:**
- Create: `src/Strela.Engine/Manager/IDownloadManager.cs`, `Manager/DownloadManager.cs`
- Modify: `Manager/DownloadSnapshot.cs`
- Test: `tests/Strela.Engine.Tests/DownloadManagerTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record DownloadSnapshot(Guid Id, string FileName, string? FilePath, string Url,
    long? TotalBytes, long DownloadedBytes, double BytesPerSecond, TimeSpan? Eta,
    DownloadStatus Status, string? StatusText, int Connections, long QueueOrder);

public interface IDownloadManager : IAsyncDisposable {
    Task InitializeAsync();                  // загрузить состояния; Downloading/Queued → Queued (автопродолжение); Paused остаётся
    Guid Add(DownloadRequest request);       // тот же URL уже Queued/Downloading/Paused → вернуть его Id, не дублировать
    void Pause(Guid id); void Resume(Guid id); void Retry(Guid id);  // Resume/Retry → Queued
    void PauseAll(); void ResumeAll();
    Task RemoveAsync(Guid id, bool deleteFile);   // deleteFile: .strela и/или готовый файл
    Task<bool> UpdateUrlAsync(Guid id, Uri newUrl); // разведка: размер совпал → сохранить прогресс, сменить CurrentUrl; иначе начать с нуля; → Queued
    void Move(Guid id, int newIndex);        // перенумеровать QueueOrder
    void ApplyOptions(EngineOptions options);// новые лимиты действуют для следующих стартов; MaxActive сразу
    IReadOnlyList<DownloadSnapshot> GetSnapshots(); // по QueueOrder; заодно обновляет SpeedMeter-ы; не блокирует
    bool HasActiveWork { get; }              // есть Downloading или Queued
    event Action<DownloadSnapshot>? Completed;
}
public sealed class DownloadManager : IDownloadManager { public DownloadManager(EngineOptions options, IStrelaLog log, HttpClient? http = null); }
```
Планировщик: не больше `MaxActiveDownloads` в Downloading, FIFO по `QueueOrder`. Каждая загрузка выполняется в `Task.Run`, у каждой свой `CancellationTokenSource`. `isPathTaken` смотрит на `FinalPath` и `PartPath` всех незавершённых загрузок. `NetworkChange.NetworkAvailabilityChanged(true)` прерывает ожидание повторов (`StatusText "Ожидание сети"` при недоступной сети). `DisposeAsync` останавливает все загрузки и сохраняет их состояние; в файлах состояния они остаются Downloading/Queued, поэтому при следующем запуске продолжатся. Если `.strela` пропал, а прогресс есть → лог Warn, сегменты сбрасываются, загрузка начинается с нуля.

- [ ] **Step 1: Тесты**

```csharp
[Fact] MaxActive_Respected                  // 5 файлов по 8 МБ @1 МБ/с, MaxActive 3 → в каждом GetSnapshots ≤ 3 Downloading; все 5 Completed
[Fact] Duplicate_ReturnsExistingId          // Add(url) дважды пока Downloading → тот же Id, 1 снимок
[Fact] PauseResume_CompletesCorrectly       // пауза на 1 с, Resume → Completed, хеш верный
[Fact] Restart_ResumesAutomatically         // Dispose посреди загрузки; новый менеджер с той же StateFolder → InitializeAsync → Completed, хеш верный, скачано повторно < 50 %
[Fact] PausedStaysPausedAfterRestart
[Fact] MissingPartFile_RestartsFromZero     // Dispose, удалить .strela, новый менеджер → Completed, хеш верный      (Review Focus 4)
[Fact] SameName_TwoFiles                    // два URL с одним именем "same.bin" одновременно → два файла "same.bin" и "same (1).bin", оба хеши верны   (Review Focus 5)
[Fact] BadFolder_ErrorNotCrash              // DownloadFolder = путь к существующему ФАЙЛУ → Error, StatusText начинается с "Нет доступа к папке"   (Review Focus 2)
[Fact] UpdateUrl_KeepsProgressWhenSizeMatches // 403 после паузы; UpdateUrlAsync(новый url с тем же содержимым) → true, Completed, хеш верный
[Fact] Remove_WithFile_DeletesFiles
[Fact] GetSnapshots_IsFast                  // 1000 загрузок в Paused → GetSnapshots() < 5 мс (среднее из 100 вызовов)
```
- [ ] **Step 2–4:** FAIL → реализовать → PASS (`--filter DownloadManagerTests`).
- [ ] **Step 5: Commit** — `feat(engine): download manager with queue and auto-resume`

---

### Task 12: Связь с внешним миром (буфер обмена, IPC)

**Files:**
- Create: `src/Strela.Engine/Integration/FileUrlDetector.cs`, `IpcProtocol.cs`, `IpcServer.cs`, `IpcClient.cs`
- Test: `tests/Strela.Engine.Tests/IntegrationTests.cs`

**Interfaces:**
- Produces:
```csharp
public static class FileUrlDetector { public static bool TryDetect(string? text, [NotNullWhen(true)] out Uri? url); }
public sealed record IpcMessage(string Url, string? Referer = null, Dictionary<string, string>? Headers = null,
    string? Cookies = null, string? FileName = null) { public DownloadRequest? ToRequest(); } // null, если не абсолютный http/https
public static class IpcProtocol {
    public static string PipeName { get; }   // "Strela.Ipc.v1." + Environment.UserName
    public static string Serialize(IReadOnlyList<IpcMessage> messages);   // JSON-массив; пустой массив = «покажи окно»
    public static IReadOnlyList<IpcMessage> Parse(string json);           // битый JSON → пустой список
}
public sealed class IpcServer : IAsyncDisposable { public IpcServer(string pipeName, IStrelaLog log); public event Action<IReadOnlyList<IpcMessage>>? Received; public void Start(); }
public static class IpcClient { public static Task<bool> SendAsync(string pipeName, IReadOnlyList<IpcMessage> messages, TimeSpan timeout); }
```
`TryDetect`: обрезанный текст — одна строка, ≤ 2048 символов, абсолютный http/https, у последнего сегмента пути расширение из списка: `zip rar 7z tar gz bz2 xz tgz zst iso img vhd vhdx ova exe msi msix appx dmg apk deb rpm jar whl nupkg mp4 mkv avi mov webm m4v mp3 flac wav m4a ogg opus pdf epub djvu docx xlsx pptx bin`. Регистр не важен.

- [ ] **Step 1: Тесты**

```csharp
[Theory]
[InlineData("https://ex.com/ubuntu-24.04.iso", true)] [InlineData("  http://ex.com/a/B.ZIP?x=1  ", true)]
[InlineData("https://ex.com/page.html", false)] [InlineData("https://ex.com/", false)]
[InlineData("ftp://ex.com/a.zip", false)] [InlineData("see https://ex.com/a.zip", false)] [InlineData("https://a.com/a.zip\nhttps://b.com/b.zip", false)]
Detect(string text, bool expected)
[Fact] Ipc_SerializeParse_RoundTrip     // с Referer, Headers, FileName
[Fact] Ipc_ParseGarbage_Empty
[Fact] Ipc_ToRequest_RejectsNonHttp     // "file:///c:/x" → null
[Fact] Ipc_ServerClient_Delivers        // уникальное имя канала; SendAsync 2 сообщений → Received с 2 элементами за < 2 с
[Fact] Ipc_ClientNoServer_ReturnsFalse  // timeout 300 мс → false
```
- [ ] **Step 2–4:** FAIL → реализовать → PASS (`--filter IntegrationTests`).
- [ ] **Step 5: Commit** — `feat(engine): clipboard url detection and named-pipe ipc`

---

### Task 13: Главное окно

**Files:**
- Create: `src/Strela.App/Services/Format.cs`, `ViewModels/MainViewModel.cs`, `ViewModels/DownloadItemViewModel.cs`, `MainWindow.xaml(.cs)`, `App.xaml(.cs)` (минимальный старт)
- Test: `tests/Strela.App.Tests/FormatTests.cs`, `MainViewModelTests.cs`, `FakeDownloadManager.cs`

**Interfaces:**
- Consumes: `IDownloadManager`, `DownloadSnapshot` (Task 11).
- Produces:
```csharp
public static class Format {   // InvariantCulture; 1 КБ = 1024; < 10 единиц → один знак после точки, иначе целое
    public static string Bytes(long? bytes);   // null → "—"; 900 → "900 Б"; 5.7 ГБ → "5.7 ГБ"; 120 МБ → "120 МБ"
    public static string Speed(double bps);    // "42 МБ/с"; 0 → ""
    public static string Eta(TimeSpan? eta);   // null → ""; 45 с → "0:45"; 3723 с → "1:02:03"
}
public sealed partial class DownloadItemViewModel : ObservableObject {
    public Guid Id { get; } public string FileName; string SizeText; double Progress /*0..1*/; string ProgressText; string SpeedText; string EtaText; DownloadStatus Status; string? FilePath; string Url;
    public void Update(DownloadSnapshot s);    // поднимает PropertyChanged только для изменившихся свойств
}
public sealed partial class MainViewModel : ObservableObject {
    public MainViewModel(IDownloadManager manager);
    public ObservableCollection<DownloadItemViewModel> Items { get; }
    public string InputText { get; set; } public string? InputHint { get; }   // "Не найдено ссылок"
    public string TotalsText { get; }          // "Всего: 42 МБ/с · Активно 1 · В очереди 1"
    public void Refresh();                     // GetSnapshots → обновить на месте / добавить / удалить по Id, порядок по QueueOrder
    public int AddLinks(string text);          // каждая строка — абсолютный http/https → Add; вернуть число добавленных; 0 → InputHint
    IRelayCommand AddFromInputCommand, PauseCommand, ResumeCommand, RetryCommand, OpenFileCommand, OpenFolderCommand, CopyUrlCommand, RemoveCommand, MoveUpCommand, MoveDownCommand;
    IAsyncRelayCommand<DownloadItemViewModel> UpdateUrlCommand;  // берёт URL из буфера обмена; нет валидного → InputHint "Скопируйте новую ссылку в буфер обмена"
    IAsyncRelayCommand<DownloadItemViewModel> RemoveWithFileCommand;  // подтверждение через Func<string,bool> ConfirmDelete (по умолчанию MessageBox)
}
```
`ProgressText`: Downloading → "68%"; Completed → "Готово"; Queued → "В очереди"; Paused → "Пауза"; Error → `StatusText`. `MainWindow`: поле ввода (Enter = добавить, `AcceptsReturn` для многострочной вставки, Ctrl+Enter тоже добавляет), `ListView` + `GridView` с `VirtualizingStackPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`; колонки Имя, Размер, Прогресс (ProgressBar + текст), Скорость, Осталось; контекстное меню из spec 6.1; двойной клик по готовой → открыть; drag&drop текста/URL → `AddLinks`; строка состояния `TotalsText`; `DispatcherTimer` 250 мс → `Refresh()`. `App`: `ThemeMode = ThemeMode.System` (подавить WPF0001), создать `DownloadManager` с папкой «Загрузки» (`SHGetKnownFolderPath(FOLDERID_Downloads)`), `InitializeAsync`, показать окно.

- [ ] **Step 1: Тесты**

```csharp
[Theory] [InlineData(null,"—")] [InlineData(900L,"900 Б")] [InlineData(6120328397L,"5.7 ГБ")] [InlineData(125829120L,"120 МБ")] Bytes(long? b, string s)
[Fact] Speed_Formats      // 44040192 → "42 МБ/с"
[Theory] [InlineData(45,"0:45")] [InlineData(3723,"1:02:03")] Eta(int s, string t)
[Fact] AddLinks_SplitsLinesIgnoresJunk   // "https://a/x.zip\nmusor\n http://b/y.iso " → 2, fake получил 2 Add
[Fact] AddLinks_None_SetsHint            // "привет" → 0, InputHint == "Не найдено ссылок"
[Fact] Refresh_UpdatesInPlace            // тот же Id с новым прогрессом → тот же экземпляр VM, Progress обновлён; лишний Id удалён
[Fact] Refresh_RaisesOnlyChangedProps    // повторный Refresh с тем же снимком → 0 событий PropertyChanged
[Fact] MoveUp_CallsManagerMove           // второй элемент → fake.Move(id, 0)
[Fact] UpdateUrl_NoClipboardUrl_SetsHint // буфер (через Func<string?> GetClipboardText) пуст → InputHint == "Скопируйте новую ссылку в буфер обмена"
[Fact] Totals_Text                       // 1 Downloading 42 МБ/с + 1 Queued → "Всего: 42 МБ/с · Активно 1 · В очереди 1"
```
- [ ] **Step 2–4:** FAIL → реализовать → PASS (`dotnet test tests/Strela.App.Tests`).
- [ ] **Step 5: Ручная проверка**

Запустить `dotnet run --project tools/Strela.TestServer`, затем `dotnet run --project src/Strela.App`. Вставить URL `test-64mb.bin` → загрузка стартует без диалогов, скорость около 16 МБ/с, окно перетаскивается и меняет размер без рывков.
- [ ] **Step 6: Commit** — `feat(app): main window with download list`

---

### Task 14: Настройки, трей, буфер обмена, один экземпляр, логи

**Files:**
- Create: `src/Strela.App/Services/AppSettings.cs`, `SettingsStore.cs`, `FileLog.cs`, `TrayIcon.cs`, `ClipboardWatcher.cs`, `Autostart.cs`, `UiLagMonitor.cs`, `Views/SettingsWindow.xaml(.cs)`, `Views/ToastWindow.xaml(.cs)`
- Modify: `App.xaml.cs`, `MainWindow.xaml(.cs)`, `MainViewModel.cs`
- Test: `tests/Strela.App.Tests/SettingsStoreTests.cs`, `FileLogTests.cs`

**Interfaces:**
- Consumes: `IDownloadManager`, `IpcServer/IpcClient/IpcProtocol`, `FileUrlDetector`.
- Produces:
```csharp
public sealed record AppSettings {
    public string DownloadFolder { get; init; } = "";  // "" = системная «Загрузки»
    public int ConnectionsPerFile { get; init; } = 16;  // clamp 1..32
    public int MaxActiveDownloads { get; init; } = 3;   // clamp 1..10
    public bool WatchClipboard { get; init; } = true;
    public bool Notifications { get; init; } = true;
    public bool StartWithWindows { get; init; } = false;
    public EngineOptions ToEngineOptions(string stateFolder);
}
public sealed class SettingsStore { public SettingsStore(string path); public AppSettings Load(); public void Save(AppSettings s); } // нет/битый файл → значения по умолчанию
public sealed class FileLog : IStrelaLog { public FileLog(string folder); } // strela.log, при > 5 МБ → strela.1.log … хранить 3; запись в фоне через Channel
public static class Autostart { public static void Set(bool enabled, string exePath); } // HKCU\Software\Microsoft\Windows\CurrentVersion\Run, значение "Strela"
public sealed class ClipboardWatcher : IDisposable { public ClipboardWatcher(); public event Action<Uri>? FileUrlCopied; } // AddClipboardFormatListener на message-only HwndSource; повтор того же URL в течение 10 с игнорируется
public sealed class UiLagMonitor : IDisposable { public UiLagMonitor(Dispatcher d, IStrelaLog log); } // каждые 50 мс BeginInvoke и замер; > 100 мс → Warn "UI lag N ms"
```
Поведение:
- **Один экземпляр:** `Mutex "Local\Strela.SingleInstance"`. Если он занят → `IpcClient.SendAsync(аргументы-URL как IpcMessage, или пустой список)` → выход. Основной экземпляр: `IpcServer.Received` → `Add` каждого и показать окно.
- **Трей:** WinForms `NotifyIcon`, подсказка `"Стрела — 42 МБ/с"` (обновляется вместе с `Refresh`), двойной клик → окно, меню «Открыть», «Пауза всех», «Продолжить все», «Выход». Закрытие окна при `HasActiveWork` → `Hide()`, иначе выход. «Выход» → `await manager.DisposeAsync()` → `Shutdown`.
- **Тост** (`ToastWindow`): без рамки, `Topmost`, `ShowActivated=false`, `ShowInTaskbar=false`, правый нижний угол рабочей области, исчезает через 8 с. Буфер обмена (если `WatchClipboard` и URL ещё нет в списке): «Скачать <имя>?» + кнопка «Скачать». Завершение загрузки (если `Notifications`): «Готово: <имя>», клик открывает папку.
- **Настройки:** немодальное окно (`Show()`, не `ShowDialog()`), поля из `AppSettings`, выбор папки через `OpenFolderDialog`. Сохранение → `SettingsStore.Save`, `manager.ApplyOptions`, `Autostart.Set`.
- Аргументы командной строки основного экземпляра → `Add`.
- Время старта: `Stopwatch` от входа в `App.OnStartup` до `MainWindow.ContentRendered` → `log.Info("startup N ms")`.
- `UiLagMonitor` запускается всегда (стоит копейки).

- [ ] **Step 1: Тесты**

```csharp
[Fact] Settings_MissingFile_Defaults          // 16, 3, WatchClipboard true
[Fact] Settings_RoundTrip_AndClamp            // ConnectionsPerFile 99 → после Load 32
[Fact] Settings_Corrupt_Defaults
[Fact] FileLog_RotatesAt5MB_Keeps3            // записать ~16 МБ → есть strela.log, .1, .2, .3; .4 нет
```
- [ ] **Step 2–4:** FAIL → реализовать → PASS.
- [ ] **Step 5: Ручная проверка** (с запущенным TestServer)
  - Скопировать в буфер `…/test-64mb.bin` → тост справа внизу, фокус не ушёл из текущего окна, «Скачать» добавляет загрузку.
  - Пока идёт загрузка, закрыть окно → оно спряталось в трей, загрузка идёт. Двойной клик по иконке трея → окно вернулось.
  - Запустить второй `Strela.exe <url>` → новое окно не появилось, ссылка добавилась в первое.
  - «Выход» во время загрузки → после повторного запуска загрузка продолжилась сама.
  - Ни одного модального окна, кроме «Удалить с файлом».
- [ ] **Step 6: Commit** — `feat(app): settings, tray, clipboard toast, single instance, logs`

---

### Task 15: Бенчмарк, нагрузка, публикация

**Files:**
- Create: `tools/Strela.Bench/Program.cs`, `docs/benchmark.md`, `publish.ps1`, `README.md`

**Interfaces:**
- Consumes: `DownloadManager` (Task 11).
- Produces: `Strela.Bench run <links.txt> [--connections N] [--runs 2] [--out <dir>] [--keep]` скачивает ссылки по очереди (`MaxActive 1`) и печатает таблицу `url | МБ | с | МБ/с` и CSV в `<out>/bench-<время>.csv`. Без `--keep` файлы удаляются. `Strela.Bench links <baseUrl> --count 1000` печатает 1000 URL тестового сервера (для нагрузки).

- [ ] **Step 1:** Реализовать Bench (без юнит-тестов — это инструмент). Проверка: `dotnet run --project tools/Strela.Bench -- run links-local.txt` на 2 ссылках TestServer печатает 2 строки с ненулевой скоростью.
- [ ] **Step 2: Нагрузочная проверка UI**

TestServer + приложение. Вставить вывод `links --count 1000` (1000 записей), `MaxActive` 10 и 20 активных загрузок. Прокрутить список, поменять размер окна. Проверка: в `%LOCALAPPDATA%\Strela\logs\strela.log` нет строк `UI lag` больше 100 мс; объём памяти виден в диспетчере задач (записать в `docs/benchmark.md`).
- [ ] **Step 3: Публикация**

`publish.ps1`: `dotnet publish src/Strela.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o publish/Strela`. Проверка: `publish/Strela/Strela.exe` запускается на машине, окно появляется меньше чем за 1 с (замерить 3 раза секундомером или `Stopwatch` в логе: `"startup N ms"` от `Main` до `ContentRendered`), память в простое записать.
- [ ] **Step 4: Шаблон сравнения с FDM**

`docs/benchmark.md`: правила из spec 7.3; набор ссылок (5–8 крупных публичных файлов с разных зеркал, ≥ 1 ГБ: дистрибутивы Linux и тестовые файлы провайдеров); таблица `ссылка | FDM по умолчанию | FDM макс. соединений | Стрела | Δ %`. Сам замер проводится вместе с пользователем: FDM запускает он.
- [ ] **Step 5:** `dotnet test` — все тесты проходят.
- [ ] **Step 6: Commit** — `feat: benchmark tool, publish script, docs`
