using System.Diagnostics.CodeAnalysis;

namespace Strela.Engine.Integration;

/// <summary>Решает, похож ли текст из буфера обмена на прямую ссылку на файл для скачивания.</summary>
public static class FileUrlDetector
{
    private const int MaxLength = 2048;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "tgz", "zst", "iso", "img", "vhd", "vhdx", "ova",
        "exe", "msi", "msix", "appx", "dmg", "apk", "deb", "rpm", "jar", "whl", "nupkg",
        "mp4", "mkv", "avi", "mov", "webm", "m4v", "mp3", "flac", "wav", "m4a", "ogg", "opus",
        "pdf", "epub", "djvu", "docx", "xlsx", "pptx", "bin",
    };

    public static bool TryDetect(string? text, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength) return false;
        if (trimmed.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var path = uri.AbsolutePath;
        var lastSegment = path[(path.LastIndexOf('/') + 1)..];
        var extension = Path.GetExtension(lastSegment).TrimStart('.');
        if (!Extensions.Contains(extension)) return false;

        url = uri;
        return true;
    }
}
