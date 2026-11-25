using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Strela.Engine.Video;

/// <summary>Где лежат программы для YouTube. <see cref="Ffmpeg"/> — папка с ffmpeg и ffprobe; <see cref="Deno"/> — исполняемый файл.</summary>
public sealed record VideoToolPaths(string YtDlp, string? Ffmpeg, string? Deno);

/// <summary>
/// yt-dlp (сам YouTube), ffmpeg (склейка видео и звука, MP3) и deno (yt-dlp исполняет им JavaScript YouTube):
/// при первой YouTube-ссылке скачиваются с официальных страниц выпусков в папку инструментов и проверяются по
/// SHA-256 там, где проект публикует суммы. yt-dlp раз в сутки обновляет сам себя (YouTube часто меняется).
/// </summary>
internal sealed partial class VideoTools
{
    private const string YtDlpReleases = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private const string FfmpegReleases = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";
    private const string DenoReleases = "https://github.com/denoland/deno/releases/latest/download/";
    private const string MacFfmpeg = "https://ffmpeg.martin-riedl.de/redirect/latest/macos/";
    private static readonly TimeSpan UpdateEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] HomebrewBins = ["/opt/homebrew/bin", "/usr/local/bin"];

    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly IStrelaLog _log;
    private readonly ToolRunner _run;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _updateChecked;

    public VideoTools(string folder, HttpClient http, IStrelaLog log, ToolRunner run)
    {
        _folder = folder;
        _http = http;
        _log = log;
        _run = run;
    }

    private static bool IsWindows => OperatingSystem.IsWindows();
    private string YtDlpPath => Path.Combine(_folder, IsWindows ? "yt-dlp.exe" : "yt-dlp");
    private string FfmpegFolder => Path.Combine(_folder, "ffmpeg");
    private string DenoPath => Path.Combine(_folder, "deno", IsWindows ? "deno.exe" : "deno");

    /// <summary>Скачивает недостающее (статус — «Подготовка YouTube: ffmpeg 45%»), раз в сутки обновляет yt-dlp.</summary>
    public async Task<VideoToolPaths> EnsureAsync(Action<string?> status, CancellationToken ct)
    {
        if (!IsWindows && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Видео с YouTube пока работает только на Windows и macOS");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_folder);
            if (!File.Exists(YtDlpPath))
                await InstallYtDlpAsync(status, ct).ConfigureAwait(false);
            else if (!_updateChecked)
                await UpdateYtDlpAsync(status, ct).ConfigureAwait(false);
            _updateChecked = true;

            var ffmpeg = SystemFfmpeg() ?? FfmpegFolder;
            if (ffmpeg == FfmpegFolder && !File.Exists(Path.Combine(FfmpegFolder, Exe("ffprobe"))))
                await InstallFfmpegAsync(status, ct).ConfigureAwait(false);
            if (!File.Exists(DenoPath))
                await InstallDenoAsync(status, ct).ConfigureAwait(false);
            return new VideoToolPaths(YtDlpPath, ffmpeg, DenoPath);
        }
        finally
        {
            status(null);
            _gate.Release();
        }
    }

    private static string Exe(string name) => IsWindows ? name + ".exe" : name;

    // ---- yt-dlp -------------------------------------------------------------------------------------------

    private async Task InstallYtDlpAsync(Action<string?> status, CancellationToken ct)
    {
        var asset = IsWindows ? "yt-dlp.exe" : "yt-dlp_macos";
        var hash = await ExpectedHashAsync(YtDlpReleases + "SHA2-256SUMS", asset, ct).ConfigureAwait(false);
        var temp = await DownloadAsync(YtDlpReleases + asset, "yt-dlp", hash, status, ct).ConfigureAwait(false);
        File.Move(temp, YtDlpPath, overwrite: true);
        await MakeRunnableAsync(YtDlpPath, ct).ConfigureAwait(false);
        _log.Info($"video: yt-dlp installed in {_folder}");
    }

    private async Task UpdateYtDlpAsync(Action<string?> status, CancellationToken ct)
    {
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(YtDlpPath) < UpdateEvery)
            return;
        status("Обновление yt-dlp…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var code = await _run(YtDlpPath, ["-U"], line => _log.Info("yt-dlp -U: " + line), line => _log.Warn("yt-dlp -U: " + line),
                                  timeout.Token).ConfigureAwait(false);
            if (code == 0)
                File.SetLastWriteTimeUtc(YtDlpPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"video: yt-dlp update failed: {ex.Message}");   // старая версия ещё может работать
        }
    }

    // ---- ffmpeg -------------------------------------------------------------------------------------------

    /// <summary>macOS: ffmpeg и ffprobe из Homebrew, если есть, — свои не нужны.</summary>
    private static string? SystemFfmpeg() =>
        OperatingSystem.IsMacOS()
            ? HomebrewBins.FirstOrDefault(d => File.Exists(Path.Combine(d, "ffmpeg")) && File.Exists(Path.Combine(d, "ffprobe")))
            : null;

    private async Task InstallFfmpegAsync(Action<string?> status, CancellationToken ct)
    {
        var staging = Path.Combine(_folder, "ffmpeg.new");
        DeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        if (IsWindows)
        {
            // Сборка с DLL: вдвое меньше статической (ffmpeg, ffprobe и ffplay делят библиотеки).
            const string asset = "ffmpeg-master-latest-win64-gpl-shared.zip";
            var hash = await ExpectedHashAsync(FfmpegReleases + "checksums.sha256", asset, ct).ConfigureAwait(false);
            var zip = await DownloadAsync(FfmpegReleases + asset, "ffmpeg", hash, status, ct).ConfigureAwait(false);
            ExtractFlat(zip, staging, entry => entry.FullName.Contains("/bin/", StringComparison.Ordinal)
                                               && !entry.Name.StartsWith("ffplay", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                var zip = await DownloadAsync($"{MacFfmpeg}{arch}/release/{tool}.zip", tool, null, status, ct).ConfigureAwait(false);
                ExtractFlat(zip, staging, entry => entry.Name == tool);
            }
        }

        foreach (var tool in new[] { "ffmpeg", "ffprobe" })
        {
            var path = Path.Combine(staging, Exe(tool));
            if (!File.Exists(path))
                throw new InvalidDataException($"В архиве нет {Exe(tool)}");
            await MakeRunnableAsync(path, ct).ConfigureAwait(false);
        }
        DeleteDirectory(FfmpegFolder);
        Directory.Move(staging, FfmpegFolder);
        _log.Info($"video: ffmpeg installed in {FfmpegFolder}");
    }

    // ---- deno ---------------------------------------------------------------------------------------------

    private async Task InstallDenoAsync(Action<string?> status, CancellationToken ct)
    {
        var target = IsWindows ? "x86_64-pc-windows-msvc"
            : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
        var asset = $"deno-{target}.zip";
        var hash = await ExpectedHashAsync(DenoReleases + asset + ".sha256sum", asset, ct).ConfigureAwait(false);
        var zip = await DownloadAsync(DenoReleases + asset, "deno", hash, status, ct).ConfigureAwait(false);
        var folder = Path.GetDirectoryName(DenoPath)!;
        DeleteDirectory(folder);
        Directory.CreateDirectory(folder);
        ExtractFlat(zip, folder, entry => entry.Name == Path.GetFileName(DenoPath));
        if (!File.Exists(DenoPath))
            throw new InvalidDataException("В архиве нет deno");
        await MakeRunnableAsync(DenoPath, ct).ConfigureAwait(false);
        _log.Info($"video: deno installed in {folder}");
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>Скачивает во временный файл рядом с инструментами, сверяет SHA-256 (если известна), возвращает путь.</summary>
    private async Task<string> DownloadAsync(string url, string label, string? sha256, Action<string?> status, CancellationToken ct)
    {
        status($"Подготовка YouTube: {label}…");
        var temp = Path.Combine(_folder, $"{label}.{Guid.NewGuid():N}.download");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);   // the shared client has no timeout
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var shown = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total is > 0 && (int)(done * 100 / total.Value) is var percent && percent != shown)
                    {
                        shown = percent;
                        status($"Подготовка YouTube: {label} {percent}%");
                    }
                }
            }

            if (sha256 is not null)
            {
                await using var check = File.OpenRead(temp);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{label}: контрольная сумма не совпала");
            }
            return temp;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            TryDelete(temp);
            throw new HttpRequestException($"{label}: сервер перестал отвечать", ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async Task<string?> ExpectedHashAsync(string sumsUrl, string asset, CancellationToken ct)
    {
        try
        {
            return ParseHash(await _http.GetStringAsync(sumsUrl, ct).ConfigureAwait(false), asset);
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"video: no checksums at {sumsUrl}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Сумма из файла сумм: строка с именем файла («hash  name», «SHA256 HASH path»); если имени нет нигде, а сумма
    /// одна — она (файл .sha256sum на один архив).
    /// </summary>
    internal static string? ParseHash(string sums, string asset)
    {
        var hashes = new List<string>();
        foreach (var line in sums.Split('\n'))
        {
            var match = Sha256().Match(line);
            if (!match.Success)
                continue;
            // Имя в конце строки целиком: «yt-dlp_macos» не должен совпасть со строкой «yt-dlp_macos.zip».
            if (Regex.IsMatch(line, $@"(^|[\s*/\\]){Regex.Escape(asset)}\s*$", RegexOptions.IgnoreCase))
                return match.Value;
            hashes.Add(match.Value);
        }
        return hashes.Count == 1 ? hashes[0] : null;
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex Sha256();

    /// <summary>Распаковывает подходящие файлы архива без их папок, затем удаляет архив.</summary>
    private static void ExtractFlat(string zip, string folder, Func<ZipArchiveEntry, bool> take)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0 || !take(entry))
                    continue;
                entry.ExtractToFile(Path.Combine(folder, entry.Name), overwrite: true);
            }
        }
        finally
        {
            TryDelete(zip);
        }
    }

    /// <summary>macOS: право на запуск и подпись (Apple Silicon не запускает неподписанное: тогда подписываем сами, ad-hoc).</summary>
    private async Task MakeRunnableAsync(string path, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                   | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        static void Ignore(string _) { }
        try
        {
            if (await _run("/usr/bin/codesign", ["--verify", path], Ignore, Ignore, ct).ConfigureAwait(false) != 0)
                await _run("/usr/bin/codesign", ["--force", "--sign", "-", path], Ignore, Ignore, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"video: codesign {path}: {ex.Message}");
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // временный файл: удалится при следующей установке
        }
    }
}
