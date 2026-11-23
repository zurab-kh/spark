namespace Strela.TestServer;

/// <summary>Как именно тестовый сервер отдаёт файл. Все значения по умолчанию — «хороший» сервер.</summary>
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
