using MonoTorrent;
using MonoTorrent.Client;
using Strela.Engine.Http;
using Strela.Engine.Jobs;
using Strela.Engine.Manager;
using Strela.Engine.Storage;

namespace Strela.Engine.Torrents;

/// <summary>
/// One run of a torrent download in the shared <see cref="TorrentService"/>: get the metadata (our .torrent copy,
/// the .torrent link, or the magnet link via peers), download every file into the download folder (a multi-file
/// torrent into a folder named after it), stop the moment it is complete — no seeding — and report
/// <see cref="DownloadStatus.Completed"/> with the file or folder as FinalPath. Cancelling <see cref="RunAsync"/>
/// pauses: the torrent is stopped, its fast-resume data saved, the data kept.
/// </summary>
internal sealed class TorrentJob : IDownloadJob
{
    internal const string SearchingText = "Поиск пиров…";
    internal const string CheckingText = "Проверка файлов…";
    internal const string NoMetadataText = "Не удалось получить метаданные торрента";
    internal const string DuplicateText = "Этот торрент уже есть в списке";
    private const string DiskFullText = "Нет места на диске";
    private const int MaxTorrentFileBytes = 20 << 20;
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FastResumeInterval = TimeSpan.FromSeconds(30);

    private readonly EngineOptions _options;
    private readonly TorrentService _service;
    private readonly HttpClient _http;
    private readonly StateStore _store;
    private readonly IStrelaLog _log;
    private readonly Func<string, bool> _isDuplicate;
    private readonly object _saveLock = new();

    private TorrentManager? _manager;
    private long _downloaded;
    private int _connections;
    private volatile string? _statusText;

    /// <param name="isDuplicate">Is another download in the list the torrent with this info-hash (hex)?</param>
    public TorrentJob(DownloadState state, EngineOptions options, TorrentService service, HttpClient http,
                      StateStore store, IStrelaLog log, Func<string, bool> isDuplicate)
    {
        State = state;
        _options = options;
        _service = service;
        _http = http;
        _store = store;
        _log = log;
        _isDuplicate = isDuplicate;
        _downloaded = state.BytesDone;
    }

    public DownloadState State { get; }

    public long DownloadedBytes => Interlocked.Read(ref _downloaded);

    public int ActiveConnections => Volatile.Read(ref _connections);

    /// <summary>«Поиск пиров…» (no metadata or no peers yet), «Проверка файлов…», «сиды N · пиры M».</summary>
    public string? StatusText => _statusText;

    public bool WaitingToRetry => false;

    public async Task<JobOutcome> RunAsync(CancellationToken ct)
    {
        State.Status = DownloadStatus.Downloading;
        State.Error = null;
        _statusText = SearchingText;
        JobOutcome outcome;
        try
        {
            outcome = await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (JobFailedException ex) when (!ct.IsCancellationRequested)
        {
            _log.Error($"torrent {Name}: {ex.Outcome.Error}", ex.InnerException);
            outcome = ex.Outcome;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            outcome = new JobOutcome(DownloadStatus.Paused);
        }
        catch (Exception ex)
        {
            _log.Error($"torrent {Name}: job failed", ex);
            outcome = IsDiskFull(ex)
                ? new JobOutcome(DownloadStatus.Paused, DiskFullText)
                : new JobOutcome(DownloadStatus.Error, $"Внутренняя ошибка: {ex.Message}");
        }

        if (_manager is { } manager)
        {
            // Paused, failed or shut down: stop (fast resume is saved), keep the data, unload.
            UpdateProgress(manager);
            await _service.RemoveAsync(manager).ConfigureAwait(false);
            _manager = null;
        }
        Volatile.Write(ref _connections, 0);
        return End(outcome);
    }

    private string Name => State.FileName ?? State.OriginalUrl;

    private async Task<JobOutcome> RunCoreAsync(CancellationToken ct)
    {
        var (torrent, magnet) = await LoadSourceAsync(ct).ConfigureAwait(false);
        if (torrent is not null)
            Adopt(torrent);
        else
            State.InfoHash ??= Hex(magnet!.InfoHashes);
        CheckDuplicate();
        Save(DownloadStatus.Downloading, null);

        var folder = string.IsNullOrEmpty(State.Folder) ? _options.DownloadFolder : State.Folder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, $"Нет доступа к папке: {folder}"), ex);
        }
        State.Folder = folder;

        ct.ThrowIfCancellationRequested();
        var manager = torrent is not null
            ? await _service.AddAsync(torrent, folder).ConfigureAwait(false)
            : await _service.AddAsync(magnet!, folder).ConfigureAwait(false);
        _manager = manager;
        await manager.StartAsync().ConfigureAwait(false);
        foreach (var peer in _options.TorrentExtraPeers)
            await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://{peer}"))).ConfigureAwait(false);

        if (!manager.HasMetadata)
            await WaitForMetadataAsync(manager, ct).ConfigureAwait(false);
        CheckPaths(manager);
        State.PartPath = TargetPath(manager);

        if (!await DownloadAsync(manager, ct).ConfigureAwait(false))
            return new JobOutcome(DownloadStatus.Paused);   // cancelled

        // Complete: stop right away (no seeding), drop the fast-resume cache, give every file its final name.
        _manager = null;
        await _service.RemoveAsync(manager, forgetCache: true).ConfigureAwait(false);
        FinishFiles(manager);
        var final = TargetPath(manager);
        State.FinalPath = final;
        State.BytesDone = State.TotalBytes ?? DownloadedBytes;
        Interlocked.Exchange(ref _downloaded, State.BytesDone);
        State.CompletedAt = DateTimeOffset.UtcNow;
        _log.Info($"torrent done {Name}: {State.BytesDone} bytes in {final}");
        return new JobOutcome(DownloadStatus.Completed, null, final);
    }

    // ---- metadata -----------------------------------------------------------------------------------------

    /// <summary>Our .torrent copy, else the magnet link, else the .torrent behind the link (fetched and copied).</summary>
    private async Task<(Torrent? Torrent, MagnetLink? Magnet)> LoadSourceAsync(CancellationToken ct)
    {
        if (State.TorrentFilePath is { } copy && File.Exists(copy))
        {
            if (Torrent.TryLoad(copy, out var saved))
                return (saved, null);
            _log.Warn($"torrent: saved copy {copy} is damaged");
        }

        if (State.Magnet is { } link)
        {
            if (!TorrentLinks.TryParseMagnet(link, out var magnet))
                throw Fail("Неверная magnet-ссылка");
            return (null, magnet);
        }

        if (Uri.TryCreate(State.OriginalUrl, UriKind.Absolute, out var url))
        {
            if (url.IsFile)
            {
                if (!Torrent.TryLoad(url.LocalPath, out var local))
                    throw Fail("Не удалось открыть .torrent-файл");
                CopyTorrent(File.ReadAllBytes(url.LocalPath));
                return (local, null);
            }
            if (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
                return (await FetchTorrentAsync(url, ct).ConfigureAwait(false), null);
        }

        throw Fail("Неизвестный источник торрента");
    }

    private async Task<Torrent> FetchTorrentAsync(Uri url, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_options.StallTimeout);
            try
            {
                var request = new DownloadRequest(url, State.Referer, State.Headers, State.Cookies, State.RequestedFileName);
                using var message = StrelaHttp.NewRequest(request, url);
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 400)
                {
                    last = new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
                    if ((int)response.StatusCode is >= 400 and < 500 and not 408 and not 429)
                        break;
                    continue;
                }
                if (response.Content.Headers.ContentLength is > MaxTorrentFileBytes)
                    throw Fail("Ссылка не ведёт на .torrent-файл");
                var bytes = await ReadLimitedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                if (!Torrent.TryLoad(bytes, out var torrent))
                    throw Fail("Ссылка не ведёт на .torrent-файл");
                CopyTorrent(bytes);
                return torrent;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException("Сервер не отвечает", ex);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
        }
        throw new JobFailedException(new JobOutcome(DownloadStatus.Error,
            $"Не удалось скачать .torrent-файл: {last?.Message ?? "ошибка сети"}"), last);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxTorrentFileBytes)
                throw Fail("Ссылка не ведёт на .torrent-файл");
        }
        return buffer.ToArray();
    }

    private async Task WaitForMetadataAsync(TorrentManager manager, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.TorrentMetadataTimeout);
        var poll = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested && !manager.HasMetadata)
            {
                UpdateProgress(manager);
                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try
        {
            await manager.WaitForMetadataAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, NoMetadataText));
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            await poll.ConfigureAwait(false);
        }

        var torrent = manager.Torrent ?? throw new JobFailedException(new JobOutcome(DownloadStatus.Error, NoMetadataText));
        if (manager.MetadataPath is { } cached && File.Exists(cached))
        {
            try
            {
                CopyTorrent(await File.ReadAllBytesAsync(cached, CancellationToken.None).ConfigureAwait(false));
            }
            catch (IOException ex)
            {
                _log.Warn($"torrent: cannot copy metadata {cached}: {ex.Message}");
            }
        }
        Adopt(torrent);
        CheckDuplicate();
        Save(DownloadStatus.Downloading, null);
        _log.Info($"torrent metadata: {torrent.Name} ({torrent.Size} bytes, {torrent.Files.Count} file(s))");
    }

    private void Adopt(Torrent torrent)
    {
        State.FileName = torrent.Name;
        State.TotalBytes = torrent.Size;
        State.InfoHash = Hex(torrent.InfoHashes);
    }

    internal static string Hex(InfoHashes hashes) => hashes.V1OrV2.ToHex();

    private void CopyTorrent(byte[] bytes)
    {
        var path = _service.TorrentCopyPath(State.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        State.TorrentFilePath = path;
    }

    private void CheckDuplicate()
    {
        if (State.InfoHash is { } hash && _isDuplicate(hash))
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, DuplicateText));
    }

    // ---- download -----------------------------------------------------------------------------------------

    /// <summary>Polls the torrent until it is complete (true) or the run is cancelled (false). Engine errors throw.</summary>
    private async Task<bool> DownloadAsync(TorrentManager manager, CancellationToken ct)
    {
        var lastSave = Environment.TickCount64;
        var lastResume = Environment.TickCount64;
        using var timer = new PeriodicTimer(PollInterval);
        while (true)
        {
            UpdateProgress(manager);
            if (manager.State == TorrentState.Error)
                throw EngineFailure(manager);
            if (IsComplete(manager))
                return true;

            var now = Environment.TickCount64;
            if (now - lastSave >= _options.StateSaveInterval.TotalMilliseconds)
            {
                lastSave = now;
                Save(DownloadStatus.Downloading, null);
            }
            if (now - lastResume >= FastResumeInterval.TotalMilliseconds)
            {
                lastResume = now;
                await SaveFastResumeAsync(manager).ConfigureAwait(false);
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private static bool IsComplete(TorrentManager m) =>
        m.HasMetadata && m.Complete
        && m.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Stopped or TorrentState.Paused;

    private void UpdateProgress(TorrentManager m)
    {
        Volatile.Write(ref _connections, m.OpenConnections);
        _statusText = m.State switch
        {
            TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.FetchingHashes => CheckingText,
            _ when !m.HasMetadata || m.State is TorrentState.Metadata or TorrentState.Starting => SearchingText,
            _ when m.Peers.Seeds + m.Peers.Leechs == 0 => SearchingText,
            _ => $"сиды {m.Peers.Seeds} · пиры {m.Peers.Leechs}",
        };

        if (!m.HasMetadata || State.TotalBytes is not { } size)
            return;
        var verified = Math.Clamp((long)(m.Progress / 100.0 * size), 0, size);
        if (m.State is TorrentState.Hashing or TorrentState.HashingPaused)
        {
            // Checking existing files: keep showing the saved progress until the check has caught up.
            Interlocked.Exchange(ref _downloaded, Math.Max(verified, State.BytesDone));
            return;
        }

        State.BytesDone = verified;
        // Between checked pieces show the data as it arrives (capped; the next checked piece corrects it).
        var received = m.Monitor.DataBytesReceived;
        if (_sessionBase is null)
            _sessionBase = verified - received;
        var shown = Math.Max(verified, Math.Min(size, _sessionBase.Value + received));
        Interlocked.Exchange(ref _downloaded, shown);
    }

    private long? _sessionBase;   // verified bytes minus bytes received when the download phase began

    private async Task SaveFastResumeAsync(TorrentManager manager)
    {
        try
        {
            var data = await manager.SaveFastResumeAsync().ConfigureAwait(false);
            var path = manager.Engine?.Settings.GetFastResumePath(manager.InfoHashes);
            if (path is null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, data.Encode()).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"torrent: fast resume not saved for {manager.Name}: {ex.Message}");
        }
    }

    private JobFailedException EngineFailure(TorrentManager m)
    {
        var error = m.Error?.Exception;
        if (error is not null && IsDiskFull(error))
            return new JobFailedException(new JobOutcome(DownloadStatus.Paused, DiskFullText), error);
        var what = m.Error?.Reason == Reason.ReadFailure ? "Ошибка чтения с диска" : "Ошибка записи на диск";
        return new JobFailedException(new JobOutcome(DownloadStatus.Error, $"{what}: {error?.Message ?? "неизвестная ошибка"}"), error);
    }

    private static bool IsDiskFull(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is IOException io && (io.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull)
                return true;
        }
        return false;
    }

    // ---- files --------------------------------------------------------------------------------------------

    /// <summary>The download's file (single-file torrent) or folder (multi-file torrent).</summary>
    private static string TargetPath(TorrentManager m) =>
        m.Files.Count == 1 && !m.Torrent!.Files[0].Path.Contains('/') && !m.Torrent.Files[0].Path.Contains('\\')
            && m.Files.Count == m.Torrent.Files.Count
            ? m.Files[0].DownloadCompleteFullPath
            : m.ContainingDirectory;

    /// <summary>A torrent whose files would land outside the download folder is refused.</summary>
    private void CheckPaths(TorrentManager m)
    {
        var root = Path.GetFullPath(State.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in m.Files)
        {
            var full = Path.GetFullPath(file.DownloadCompleteFullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw Fail("Торрент содержит недопустимые пути к файлам");
        }
    }

    /// <summary>After the stop: partial files get their final names; empty files exist.</summary>
    private void FinishFiles(TorrentManager m)
    {
        foreach (var file in m.Files)
        {
            try
            {
                var final = file.DownloadCompleteFullPath;
                if (File.Exists(final))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                if (File.Exists(file.DownloadIncompleteFullPath))
                    File.Move(file.DownloadIncompleteFullPath, final);
                else if (file.Length == 0)
                    File.Create(final).Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"torrent: cannot finish {file.FullPath}: {ex.Message}");
            }
        }
    }

    // ---- state --------------------------------------------------------------------------------------------

    private JobOutcome End(JobOutcome outcome)
    {
        _statusText = null;
        Save(outcome.Status, outcome.Error);
        return outcome;
    }

    private void Save(DownloadStatus status, string? error)
    {
        lock (_saveLock)
        {
            State.Status = status;
            State.Error = error;
            try
            {
                _store.Save(State);
            }
            catch (Exception ex)
            {
                _log.Warn($"state: save failed for {State.FileName ?? State.OriginalUrl}: {ex.Message}");
            }
        }
    }

    private static JobFailedException Fail(string text) => new(new JobOutcome(DownloadStatus.Error, text));
}
