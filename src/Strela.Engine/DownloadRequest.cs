namespace Strela.Engine;

/// <summary>Что и как скачивать: адрес плюс заголовки, которые нужно отправлять с каждым запросом.</summary>
public sealed record DownloadRequest(
    Uri Url,
    string? Referer = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Cookies = null,
    string? FileName = null);
