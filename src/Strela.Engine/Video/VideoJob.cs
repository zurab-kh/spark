using Strela.Engine.Jobs;
using Strela.Engine.Manager;
using Strela.Engine.Storage;

namespace Strela.Engine.Video;

/// <summary>
/// One run of a YouTube download: make sure the tools are there (<see cref="VideoTools"/>), then let yt-dlp fetch
/// the video and audio streams into the download's own temp folder and merge them into the download folder.
/// Progress comes from yt-dlp's output in our templates (<see cref="YtDlp"/>). Cancelling kills yt-dlp, which
/// leaves its .part files; the next run continues them.
/// </summary>
internal sealed class VideoJob : IDownloadJob
{
    internal const string StartingText = "Получение сведений о видео…";
    internal const string MergingText = "Склейка видео и звука…";
    internal const string ConvertingText = "Конвертация в MP3…";

    private readonly EngineOptions _options;
    private readonly VideoTools _tools;
    private readonly ToolRunner _run;
    private readonly StateStore _store;
    private readonly IStrelaLog _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Done, long? Total, bool Finished)> _parts = [];

    private int _expectedParts = 1;
    private long _downloaded;
    private volatile string? _statusText;
    private string? _lastError;
    private string? _finalPath;

    public VideoJob(DownloadState state, EngineOptions options, VideoTools tools, ToolRunner run, StateStore store, IStrelaLog log)
    {
        State = state;
        _options = options;
        _tools = tools;
        _run = run;
        _store = store;
        _log = log;
        _downloaded = state.BytesDone;
    }

    public DownloadState State { get; }

    public long DownloadedBytes => Interlocked.Read(ref _downloaded);

    public int ActiveConnections => 0;

    public string? StatusText => _statusText;

    public bool WaitingToRetry => false;

    /// <summary>The download's temp folder: yt-dlp's .part files and streams before the merge.</summary>
    internal static string TempFolder(EngineOptions options, Guid id) => Path.Combine(options.StateFolder, "video", id.ToString("N"));

    private string Name => State.FileName ?? State.OriginalUrl;

    private VideoQuality Quality => State.VideoQuality ?? _options.VideoQuality;

    public async Task<JobOutcome> RunAsync(CancellationToken ct)
    {
        State.Status = DownloadStatus.Downloading;
        State.Error = null;
        _statusText = StartingText;
        JobOutcome outcome;
        try
        {
            outcome = await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            outcome = new JobOutcome(DownloadStatus.Paused);
        }
        catch (Exception ex)
        {
            _log.Error($"video {Name}: job failed", ex);
            outcome = new JobOutcome(DownloadStatus.Error, ex switch
            {
                HttpRequestException or InvalidDataException => $"Не удалось скачать компоненты для YouTube: {ex.Message}",
                PlatformNotSupportedException => ex.Message,
                System.ComponentModel.Win32Exception => $"Не удалось запустить yt-dlp: {ex.Message}",
                _ => $"Внутренняя ошибка: {ex.Message}",
            });
        }

        _statusText = null;
        State.BytesDone = DownloadedBytes;
        State.Status = outcome.Status;
        State.Error = outcome.Error;
        Save();
        return outcome;
    }

    private async Task<JobOutcome> RunCoreAsync(CancellationToken ct)
    {
        var folder = string.IsNullOrEmpty(State.Folder) ? _options.DownloadFolder : State.Folder;
        var temp = TempFolder(_options, State.Id);
        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JobOutcome(DownloadStatus.Error, $"Нет доступа к папке: {folder}");
        }
        State.Folder = folder;
        State.VideoQuality ??= _options.VideoQuality;

        var tools = _options.VideoToolsOverride
            ?? await _tools.EnsureAsync(text => _statusText = text ?? StartingText, ct).ConfigureAwait(false);
        _statusText = StartingText;

        var args = YtDlp.Arguments(new Uri(State.OriginalUrl), Quality, folder, temp, tools);
        _log.Info($"video {Name}: yt-dlp {string.Join(' ', args)}");
        var code = await _run(tools.YtDlp, args, OnStdout, OnStderr, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (code == 0 && (_finalPath is null || !File.Exists(_finalPath)))
        {
            // The printed path did not come through (an encoding we did not expect): find the file by the video id.
            var found = FindDownloaded(folder);
            _log.Warn($"video {Name}: reported path {_finalPath ?? "none"} not found, using {found ?? "nothing"}");
            _finalPath = found;
        }
        if (code != 0 || _finalPath is null || !File.Exists(_finalPath))
        {
            var error = YtDlp.ErrorText(_lastError ?? (code == 0 ? "yt-dlp не сообщил, куда сохранил файл" : null));
            _log.Warn($"video {Name}: exit {code}: {_lastError}");
            return error == "Нет места на диске"
                ? new JobOutcome(DownloadStatus.Paused, error)
                : new JobOutcome(DownloadStatus.Error, error);
        }

        State.FinalPath = _finalPath;
        State.FileName = Path.GetFileName(_finalPath);
        State.TotalBytes = new FileInfo(_finalPath).Length;
        Interlocked.Exchange(ref _downloaded, State.TotalBytes.Value);
        State.CompletedAt = DateTimeOffset.UtcNow;
        TryDeleteFolder(temp);
        _log.Info($"video done {Name}: {State.TotalBytes} bytes in {_finalPath}");
        return new JobOutcome(DownloadStatus.Completed, null, _finalPath);
    }

    /// <summary>The newest finished file of this video («… [ID].mp4») in <paramref name="folder"/>, or null.</summary>
    private string? FindDownloaded(string folder)
    {
        if (!Uri.TryCreate(State.OriginalUrl, UriKind.Absolute, out var url) || !VideoLinks.TryGetYouTubeId(url, out var id))
            return null;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles($"*[{id}].*")
                .Where(f => f.Extension is not (".part" or ".ytdl" or ".temp") && !f.Name.Contains(".part-", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnStdout(string line)
    {
        if (YtDlp.TryParseProgress(line, out var p))
        {
            OnProgress(p);
        }
        else if (YtDlp.TryParseInfo(line, out var info))
        {
            lock (_lock)
                _expectedParts = Math.Max(1, info.FormatId.Split('+').Length);
            if (info.Size is { } size)
                State.TotalBytes = size;
            if (info.Title.Length > 0 && State.FileName is null)
                State.FileName = info.Title;   // the real name (with extension) comes with the file
            _statusText = null;
            Save();
        }
        else if (YtDlp.TryParseFile(line, out var path))
        {
            _finalPath = path;
        }
    }

    private void OnStderr(string line)
    {
        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            _lastError = line;
        else if (line.Length > 0)
            _log.Info($"video {Name}: {line}");
    }

    private void OnProgress(YtDlp.Progress p)
    {
        long done;
        long? total;
        bool allFinished;
        lock (_lock)
        {
            _parts.TryGetValue(p.FormatId, out var part);
            var partTotal = p.Total ?? part.Total;
            var partDone = p.Downloaded ?? (p.Finished ? partTotal ?? part.Done : part.Done);
            _parts[p.FormatId] = (Math.Max(part.Done, partDone), partTotal, part.Finished || p.Finished);

            done = _parts.Values.Sum(x => x.Done);
            total = _parts.Count >= _expectedParts && _parts.Values.All(x => x.Total is not null)
                ? _parts.Values.Sum(x => x.Total!.Value)
                : null;
            allFinished = _parts.Count >= _expectedParts && _parts.Values.All(x => x.Finished);
        }

        if (total is { } t && t >= done)
            State.TotalBytes = t;
        if (State.TotalBytes is { } known && done > known)
            State.TotalBytes = done;
        Interlocked.Exchange(ref _downloaded, done);
        _statusText = allFinished ? (Quality == VideoQuality.AudioMp3 ? ConvertingText : MergingText) : null;
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                _store.Save(State);
            }
            catch (Exception ex)
            {
                _log.Warn($"state: save failed for {Name}: {ex.Message}");
            }
        }
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"video: cannot delete {folder}: {ex.Message}");
        }
    }
}
