using System.Diagnostics.CodeAnalysis;
using MonoTorrent;

namespace Strela.Engine.Torrents;

/// <summary>Что считается торрентом: magnet-ссылка, http(s)-ссылка на .torrent, локальный .torrent-файл.</summary>
public static class TorrentLinks
{
    public const string Extension = ".torrent";
    public const string MimeType = "application/x-bittorrent";

    /// <summary>magnet:?xt=urn:btih:… (v1) или urn:btmh:… (v2), который MonoTorrent может разобрать.</summary>
    public static bool IsMagnet(string? text) => TryParseMagnet(text, out _);

    /// <summary>Имя из параметра dn magnet-ссылки; null, если это не magnet или имени нет.</summary>
    public static string? MagnetName(string? text) =>
        TryParseMagnet(text, out var magnet) && !string.IsNullOrWhiteSpace(magnet.Name) ? magnet.Name : null;

    internal static bool TryParseMagnet(string? text, [NotNullWhen(true)] out MagnetLink? magnet)
    {
        magnet = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return MagnetLink.TryParse(trimmed, out magnet) && magnet is not null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>http(s)-адрес, путь которого (или имя файла от браузера) оканчивается на .torrent.</summary>
    public static bool IsTorrentUrl(Uri url, string? fileName = null)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            return false;
        return url.AbsolutePath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || (fileName?.Trim().EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Абсолютный путь к существующему локальному .torrent-файлу (в том числе file:///…).</summary>
    public static bool IsTorrentFile(string? text, [NotNullWhen(true)] out string? path)
    {
        path = null;
        var trimmed = text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 32767)
            return false;
        string candidate;
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || !uri.IsFile)
                return false;
            candidate = uri.LocalPath;
        }
        else
        {
            candidate = trimmed;
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidate) || !candidate.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(candidate))
                return false;
            path = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>Торрент-запрос (magnet, локальный .torrent, http(s)-ссылка на .torrent)?</summary>
    public static bool IsTorrentRequest(DownloadRequest request)
    {
        var url = request.Url;
        if (!url.IsAbsoluteUri)
            return false;
        if (url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            return true;
        if (url.IsFile)
            return url.LocalPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
        return IsTorrentUrl(url, request.FileName);
    }

    /// <summary>Текст строки → адрес для <see cref="DownloadRequest"/>: magnet, локальный .torrent (file:) или null.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        var trimmed = text?.Trim();
        if (TryParseMagnet(trimmed, out _))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out url);
        if (IsTorrentFile(trimmed, out var path))
            return Uri.TryCreate(path, UriKind.Absolute, out url);
        return false;
    }
}
