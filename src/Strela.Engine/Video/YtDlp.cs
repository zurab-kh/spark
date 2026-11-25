using System.Globalization;

namespace Strela.Engine.Video;

/// <summary>Качество видео YouTube (настройка; запоминается в загрузке при добавлении, чтобы докачка шла тем же форматом).</summary>
public enum VideoQuality { Best, P1080, P720, AudioMp3 }

/// <summary>Аргументы yt-dlp и разбор строк, которые он печатает по нашим шаблонам.</summary>
internal static class YtDlp
{
    internal const string ProgressTag = "[spark]";
    internal const string InfoTag = "[spark-info]";
    internal const string FileTag = "[spark-file]";

    /// <summary>Сортировка форматов: выше разрешение, при равном — H.264 и AAC (играют везде, в том числе в QuickTime).</summary>
    private static string Sort(int? maxHeight) => $"res{(maxHeight is { } h ? ":" + h : "")},fps,vcodec:h264,acodec:m4a";

    public static List<string> Arguments(Uri url, VideoQuality quality, string folder, string tempFolder, VideoToolPaths tools)
    {
        var args = new List<string>
        {
            "--ignore-config", "--no-playlist", "--newline", "--progress", "--color", "never", "--no-mtime", "--continue",
            // Иначе на Windows вывод (название, путь к файлу) идёт в кодировке ANSI, и кириллица превращается в «�».
            "--encoding", "utf-8",
            "--progress-template",
            $"download:{ProgressTag} %(info.format_id)s %(progress.status)s %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
            "--print", $"before_dl:{InfoTag} %(format_id)s %(filesize,filesize_approx)s %(title)s",
            "--print", $"after_move:{FileTag} %(filepath)s",
            "-P", "home:" + folder,
            "-P", "temp:" + tempFolder,
            "-o", "%(title).150B [%(id)s].%(ext)s",
        };
        if (tools.Ffmpeg is { } ffmpeg)
            args.AddRange(["--ffmpeg-location", ffmpeg]);
        if (tools.Deno is { } deno)
            args.AddRange(["--js-runtimes", "deno:" + deno]);

        switch (quality)
        {
            case VideoQuality.AudioMp3:
                args.AddRange(["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0"]);
                break;
            default:
                int? height = quality switch { VideoQuality.P1080 => 1080, VideoQuality.P720 => 720, _ => null };
                args.AddRange(["-f", "bv*+ba/b", "-S", Sort(height), "--merge-output-format", "mp4"]);
                break;
        }

        args.Add(url.AbsoluteUri);
        return args;
    }

    /// <summary>«[spark] 137 downloading 1048576 52428800 NA».</summary>
    internal sealed record Progress(string FormatId, bool Finished, long? Downloaded, long? Total);

    /// <summary>«[spark-info] 137+140 54525952 Title with spaces».</summary>
    internal sealed record Info(string FormatId, long? Size, string Title);

    public static bool TryParseProgress(string line, out Progress progress)
    {
        progress = null!;
        if (!line.StartsWith(ProgressTag + " ", StringComparison.Ordinal))
            return false;
        var parts = line[(ProgressTag.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return false;
        progress = new Progress(parts[0], parts[1] == "finished", Number(parts[2]), Number(parts[3]) ?? Number(parts[4]));
        return true;
    }

    public static bool TryParseInfo(string line, out Info info)
    {
        info = null!;
        if (!line.StartsWith(InfoTag + " ", StringComparison.Ordinal))
            return false;
        var parts = line[(InfoTag.Length + 1)..].Split(' ', 3);
        if (parts.Length < 2)
            return false;
        info = new Info(parts[0], Number(parts[1]), parts.Length > 2 ? parts[2].Trim() : "");
        return true;
    }

    public static bool TryParseFile(string line, out string path)
    {
        path = "";
        if (!line.StartsWith(FileTag + " ", StringComparison.Ordinal))
            return false;
        path = line[(FileTag.Length + 1)..].Trim();
        return path.Length > 0;
    }

    /// <summary>yt-dlp печатает байты целыми или дробными («1234.0»), неизвестное — «NA» или «None».</summary>
    private static long? Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? (long)d : null;

    /// <summary>Последняя строка «ERROR: …» из stderr → понятная пользователю причина.</summary>
    public static string ErrorText(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "yt-dlp завершился с ошибкой";
        var e = error.StartsWith("ERROR:", StringComparison.Ordinal) ? error[6..].Trim() : error.Trim();
        bool Has(string s) => e.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("Private video")) return "Это приватное видео";
        if (Has("confirm your age") || Has("age-restricted") || Has("inappropriate for some users")) return "Видео с ограничением по возрасту: нужен вход в аккаунт";
        if (Has("not a bot")) return "YouTube просит подтвердить, что вы не робот: попробуйте позже";
        if (Has("members-only") || Has("Join this channel")) return "Видео только для спонсоров канала";
        if (Has("live event will begin") || Has("Premieres in") || Has("is upcoming")) return "Трансляция или премьера ещё не началась";
        if (Has("not available in your country") || Has("blocked it in your country")) return "Видео недоступно в вашей стране";
        if (Has("Video unavailable") || Has("This video has been removed") || Has("does not exist")) return "Видео недоступно";
        if (Has("No space left") || Has("Errno 28")) return "Нет места на диске";
        if (Has("Unable to download") || Has("timed out") || Has("getaddrinfo") || Has("Connection")) return "Ошибка сети: " + Short(e);
        return Short(e);
    }

    private static string Short(string s)
    {
        // «[youtube] dQw4w9WgXcQ: Video unavailable» → без префикса извлекателя.
        if (s.StartsWith('[') && s.IndexOf("] ", StringComparison.Ordinal) is var close and > 0)
        {
            s = s[(close + 2)..];
            if (s.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0 and <= 20)
                s = s[(colon + 2)..];
        }
        return s.Length <= 160 ? s : s[..157] + "…";
    }
}
