using System.Diagnostics.CodeAnalysis;

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
}
