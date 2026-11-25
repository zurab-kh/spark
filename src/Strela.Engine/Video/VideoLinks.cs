using System.Diagnostics.CodeAnalysis;

namespace Strela.Engine.Video;

/// <summary>Что считается ссылкой на видео YouTube: watch?v=, youtu.be/, shorts/, live/, embed/ (в том числе m. и music.).</summary>
public static class VideoLinks
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com",
    };

    private static readonly string[] PathPrefixes = ["/shorts/", "/live/", "/embed/", "/v/"];

    /// <summary>Ссылка на одно видео YouTube → его id из 11 символов.</summary>
    public static bool TryGetYouTubeId(Uri? url, [NotNullWhen(true)] out string? id)
    {
        id = null;
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            return false;

        string? candidate = null;
        if (url.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            candidate = url.AbsolutePath.Trim('/');
        }
        else if (Hosts.Contains(url.Host))
        {
            var path = url.AbsolutePath;
            if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
                candidate = QueryValue(url, "v");
            else if (PathPrefixes.FirstOrDefault(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
                candidate = path[prefix.Length..].Split('/')[0];
        }

        if (candidate is null || !IsVideoId(candidate))
            return false;
        id = candidate;
        return true;
    }

    public static bool IsYouTube(Uri? url) => TryGetYouTubeId(url, out _);

    /// <summary>Текст строки (вставка, буфер обмена) → каноническая ссылка на видео.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 2048 || trimmed.Any(char.IsWhiteSpace))
            return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || !TryGetYouTubeId(parsed, out var id))
            return false;
        url = Canonical(id);
        return true;
    }

    /// <summary>https://www.youtube.com/watch?v=ID: по ней ищутся дубликаты, её получает yt-dlp (без плейлиста и времени).</summary>
    public static Uri Canonical(string id) => new($"https://www.youtube.com/watch?v={id}");

    private static bool IsVideoId(string s) =>
        s.Length == 11 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string? QueryValue(Uri url, string name)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
