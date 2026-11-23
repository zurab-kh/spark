using System.Diagnostics.CodeAnalysis;
using Strela.Engine.Torrents;

namespace Strela.App.Services;

public static class LinkParser
{
    /// <summary>Абсолютный http/https адрес. Task 12: для буфера обмена заменить на FileUrlDetector.TryDetect.</summary>
    public static bool TryParseHttp(string? text, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        url = u;
        return true;
    }

    /// <summary>
    /// То, что можно скачать: абсолютный http/https адрес, magnet-ссылка или путь к существующему .torrent-файлу
    /// (для файла — file:-адрес).
    /// </summary>
    public static bool TryParseLink(string? text, [NotNullWhen(true)] out Uri? url) =>
        TryParseHttp(text, out url) || TorrentLinks.TryParse(text, out url);
}
