using System.Text.Json;

namespace Strela.Engine.Integration;

/// <summary>Одно сообщение от расширения браузера или второго экземпляра приложения.</summary>
public sealed record IpcMessage(
    string Url,
    string? Referer = null,
    Dictionary<string, string>? Headers = null,
    string? Cookies = null,
    string? FileName = null)
{
    /// <summary>
    /// Запрос на скачивание или null. Принимаются абсолютные http/https-адреса, magnet-ссылки (второй экземпляр,
    /// открытый по ссылке из браузера) и пути к существующим локальным .torrent-файлам (двойной клик по файлу);
    /// всё остальное (file: к другим файлам, javascript: и т. п.) отбрасывается.
    /// </summary>
    public DownloadRequest? ToRequest()
    {
        if (Torrents.TorrentLinks.TryParse(Url, out var torrent))
            return new DownloadRequest(torrent, Referer, Headers, Cookies, FileName);
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return new DownloadRequest(uri, Referer, Headers, Cookies, FileName);
    }
}

public static class IpcProtocol
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string PipeName => "Spark.Ipc.v1." + Environment.UserName;

    /// <summary>JSON-массив; пустой массив означает «показать окно».</summary>
    public static string Serialize(IReadOnlyList<IpcMessage> messages) =>
        JsonSerializer.Serialize(messages, Options);

    /// <summary>Разбирает JSON-массив; битый JSON или не массив дают пустой список, записи без Url пропускаются.</summary>
    public static IReadOnlyList<IpcMessage> Parse(string json)
    {
        var result = new List<IpcMessage>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var message = element.Deserialize<IpcMessage>(Options);
                    if (message is not null && !string.IsNullOrEmpty(message.Url)) result.Add(message);
                }
                catch (JsonException) { }
            }
        }
        catch (JsonException)
        {
            result.Clear();
        }
        return result;
    }
}
