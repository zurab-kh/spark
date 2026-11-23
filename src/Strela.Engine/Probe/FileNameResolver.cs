using System.Net.Http.Headers;

namespace Strela.Engine.Probe;

public static class FileNameResolver
{
    private const string Fallback = "download";
    private const int MaxLength = 200;
    private const int MaxExtensionLength = 20;
    private const string InvalidChars = "<>:\"/\\|?*";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>filename* → filename → last URL path segment (decoded) → "download".</summary>
    public static string Resolve(ContentDispositionHeaderValue? cd, Uri finalUri)
    {
        var fromHeader = cd?.FileNameStar;
        if (string.IsNullOrWhiteSpace(fromHeader))
            fromHeader = cd?.FileName?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(fromHeader))
            return Sanitize(fromHeader);

        var segment = finalUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        return Sanitize(segment is null ? "" : Uri.UnescapeDataString(segment));
    }

    public static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (InvalidChars.Contains(chars[i]) || char.IsControl(chars[i]))
                chars[i] = '_';
        }

        var result = new string(chars).TrimEnd('.', ' ');
        if (result.Length == 0)
            return Fallback;

        var dot = result.IndexOf('.');
        var baseName = dot < 0 ? result : result[..dot];
        if (ReservedNames.Contains(baseName.TrimEnd(' ')))
            result = "_" + result;

        return result.Length <= MaxLength ? result : Truncate(result);
    }

    public static string ReserveUniquePath(string folder, string fileName, Func<string, bool> isTaken)
    {
        var (stem, ext) = SplitExtension(fileName);
        for (var n = 0; ; n++)
        {
            var candidate = n == 0 ? fileName : $"{stem} ({n}){ext}";
            var path = Path.Combine(folder, candidate);
            if (!File.Exists(path) && !File.Exists(path + ".strela") && !isTaken(path))
                return path;
        }
    }

    private static string Truncate(string name)
    {
        var (stem, ext) = SplitExtension(name);
        if (ext.Length > MaxExtensionLength)
            (stem, ext) = (name, "");

        var stemLength = MaxLength - ext.Length;
        if (char.IsHighSurrogate(stem[stemLength - 1]))
            stemLength--;
        stem = stem[..stemLength].TrimEnd('.', ' ');

        return stem.Length == 0 ? Fallback : stem + ext;
    }

    private static (string Stem, string Ext) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, "") : (name[..dot], name[dot..]);
    }
}
