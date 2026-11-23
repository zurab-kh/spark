using System.Net;
using Strela.Engine.Manager;
using Strela.Engine.Policies;
using Strela.Engine.Probe;
using Strela.Engine.Segments;
using Strela.Engine.Storage;

namespace Strela.Engine.Jobs;

/// <summary>
/// Downloads one file over several connections: probe → part file → workers with work stealing →
/// periodic state saves → rename to the final name. Cancelling <see cref="RunAsync"/> pauses the download.
/// <para>
/// Locking: <c>_segmentsLock</c> guards the segment list and segment ownership; steals (split + add) and the
/// "all done" check happen under it, and so does the state snapshot, so a freshly stolen tail is never missed.
/// <c>_saveLock</c> serialises state saves with single-stream truncation. Order: _saveLock → _segmentsLock → Segment.
/// </para>
/// </summary>
internal sealed class DownloadJob
{
    private const string DiskFullText = "Нет места на диске";

    private readonly HttpClient _http;
    private readonly HostLimits _limits;
    private readonly StateStore _store;
    private readonly IStrelaLog _log;
    private readonly Func<string, bool> _isPathTaken;

    private readonly object _segmentsLock = new();
    private readonly List<Segment> _segments = [];
    private readonly HashSet<Segment> _owned = [];
    private readonly object _saveLock = new();

    private PartFile? _part;
    private CancellationTokenSource? _stop;
    private JobOutcome? _failure;
    private long _downloaded;
    private int _activeConnections;
    private volatile string? _statusText;

    public DownloadJob(DownloadState state, EngineOptions options, HttpClient http, HostLimits limits,
                       StateStore store, IStrelaLog log, Func<string, bool> isPathTaken)
    {
        State = state;
        Options = options;
        _http = http;
        _limits = limits;
        _store = store;
        _log = log;
        _isPathTaken = isPathTaken;
    }

    public DownloadState State { get; }

    /// <summary>Sum over segments of (Position − Start).</summary>
    public long DownloadedBytes => Interlocked.Read(ref _downloaded);

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>What the job is waiting for, e.g. "Повтор через 8 с"; null while it simply downloads.</summary>
    public string? StatusText => _statusText;

    internal EngineOptions Options { get; }
    internal HttpClient Http => _http;
    internal DownloadRequest Request { get; private set; } = null!;
    internal Uri Uri { get; private set; } = null!;
    internal bool SingleStream { get; private set; }
    internal bool KnownLength => State.TotalBytes is not null;

    public async Task<JobOutcome> RunAsync(CancellationToken ct)
    {
        State.Status = DownloadStatus.Downloading;
        State.Error = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop = stop;
        _failure = null;
        try
        {
            var probe = await ProbeAsync(stop.Token);
            Prepare(probe);
            OpenPartFile();
            var outcome = await TransferAsync(stop, ct);
            return outcome.Status == DownloadStatus.Completed ? outcome : End(outcome);
        }
        catch (JobFailedException ex)
        {
            _log.Error($"{State.FileName ?? State.CurrentUrl}: {ex.Outcome.Error}", ex.InnerException);
            return End(ex.Outcome);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Pause. A cancelled request may surface as IOException / HttpRequestException, not only OCE.
            return End(new JobOutcome(DownloadStatus.Paused));
        }
        catch (Exception ex)
        {
            _log.Error($"{State.FileName ?? State.CurrentUrl}: job failed", ex);
            return End(new JobOutcome(DownloadStatus.Error, $"Внутренняя ошибка: {ex.Message}"));
        }
        finally
        {
            _part?.Dispose();
            _part = null;
            _stop = null;
        }
    }

    // ---- setup --------------------------------------------------------------------------------------------

    private async Task<ProbeResult> ProbeAsync(CancellationToken token)
    {
        var streak = new RetryStreak();
        while (true)
        {
            Exception error;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(Options.StallTimeout);   // HttpClient itself has no timeout
                try
                {
                    var result = await Prober.ProbeAsync(_http, State.ToRequest(), deadline.Token);
                    if ((int)result.Status < 400)
                        return result;
                    error = new HttpStatusException(result.Status, null);
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    error = new TimeoutException("Сервер не отвечает", ex);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    error = ex;
                }
            }

            await BackOffAsync(streak, error, token);
        }
    }

    private void Prepare(ProbeResult probe)
    {
        State.CurrentUrl = probe.FinalUri.ToString();
        if (string.IsNullOrEmpty(State.Folder))
            State.Folder = Options.DownloadFolder;
        try
        {
            Directory.CreateDirectory(State.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, $"Папка недоступна: {ex.Message}"), ex);
        }

        var isNew = State.TotalBytes is null && State.Segments.Count == 0;
        if (isNew)
        {
            State.TotalBytes = probe.TotalBytes;
            State.SupportsRanges = probe.SupportsRanges;
            State.ETag = probe.ETag;
            State.LastModified = probe.LastModified;
            var final = FileNameResolver.ReserveUniquePath(State.Folder, probe.FileName, IsTakenByOthers);
            State.FileName = Path.GetFileName(final);
            State.PartPath = final + ".strela";
        }
        // Task 10: on resume compare ETag / Last-Modified / size with the probe ("restart: file changed").

        Uri = probe.FinalUri;
        Request = State.ToRequest();
        SingleStream = !State.SupportsRanges || State.TotalBytes is null;

        var segments = new List<Segment>();
        if (SingleStream)
        {
            // No resume without ranges: the worker truncates and starts from 0 (RestartSingleStream).
            segments.Add(new Segment(0, State.TotalBytes ?? long.MaxValue, 0));
            _log.Info($"single-stream: {State.FileName} (ranges: {State.SupportsRanges}, size: {State.TotalBytes?.ToString() ?? "unknown"})");
        }
        else if (State.Segments.Count > 0 && File.Exists(State.PartPath))
        {
            foreach (var s in State.Segments)
                segments.Add(new Segment(s.Start, s.End, s.Position));
        }
        else
        {
            if (State.Segments.Count > 0)
                _log.Warn($"{State.FileName}: part file is missing, starting over");
            segments = SegmentPlanner.InitialSplit(State.TotalBytes!.Value, ConnectionCeiling(), Options.MinSegmentBytes);
        }

        lock (_segmentsLock)
        {
            _segments.Clear();
            _segments.AddRange(segments);
            _owned.Clear();
            long downloaded = 0;
            foreach (var s in _segments)
                downloaded += s.Position - s.Start;
            Interlocked.Exchange(ref _downloaded, downloaded);
        }
    }

    private void OpenPartFile()
    {
        try
        {
            _part = PartFile.OpenOrCreate(State.PartPath!, SingleStream ? null : State.TotalBytes);
        }
        catch (IOException ex)
        {
            throw LocalFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, $"Нет доступа к файлу: {ex.Message}"), ex);
        }
    }

    private int ConnectionCeiling() => Math.Max(1, _limits.Ceiling(Uri.Host, Options.ConnectionsPerFile));

    // ---- transfer -----------------------------------------------------------------------------------------

    private async Task<JobOutcome> TransferAsync(CancellationTokenSource stop, CancellationToken ct)
    {
        int workerCount;
        lock (_segmentsLock)
            workerCount = SingleStream ? 1 : Math.Max(1, Math.Min(ConnectionCeiling(), _segments.Count));
        _log.Info($"start {State.FileName}: {workerCount} connection(s), {State.TotalBytes?.ToString() ?? "?"} bytes");

        using var saverStop = new CancellationTokenSource();
        var saver = SaveLoopAsync(saverStop.Token);

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var worker = new SegmentWorker(this, i, stop.Token);
            workers[i] = Task.Run(worker.RunAsync, CancellationToken.None);
        }

        await Task.WhenAll(workers);   // workers never throw
        saverStop.Cancel();
        await saver;

        if (Volatile.Read(ref _failure) is { } failure)
            return failure;
        if (AllDone())
            return Complete();
        if (ct.IsCancellationRequested)
            return new JobOutcome(DownloadStatus.Paused);

        _log.Error($"{State.FileName}: workers stopped with unfinished segments");
        return new JobOutcome(DownloadStatus.Error, "Внутренняя ошибка: загрузка остановилась");
    }

    /// <summary>
    /// Next segment for a worker that finished <paramref name="finished"/> (or is just starting): an unfinished
    /// segment nobody owns, else a tail stolen from the segment with the most unreserved bytes; null = no work.
    /// </summary>
    internal Segment? TakeWork(Segment? finished)
    {
        Segment stolen;
        lock (_segmentsLock)
        {
            if (finished is not null)
                _owned.Remove(finished);

            foreach (var s in _segments)
            {
                if (!s.IsDone && _owned.Add(s))
                    return s;
            }

            // Split and add under the same lock as AllDone and the state snapshot.
            var tail = SegmentPlanner.Steal(_segments, Options.MinSegmentBytes);
            if (tail is null)
                return null;
            _segments.Add(tail);
            _owned.Add(tail);
            stolen = tail;
        }

        _log.Info($"split: [{stolen.Start}, {stolen.End})");
        return stolen;
    }

    /// <summary>Single-stream (re)start: empty part file, one segment from 0, zero progress.</summary>
    internal Segment RestartSingleStream()
    {
        var segment = new Segment(0, State.TotalBytes ?? long.MaxValue, 0);
        lock (_saveLock)
        {
            try
            {
                _part!.Truncate();
            }
            catch (IOException ex)
            {
                throw LocalFailure(ex);
            }

            lock (_segmentsLock)
            {
                _segments.Clear();
                _segments.Add(segment);
                Interlocked.Exchange(ref _downloaded, 0);
            }
        }

        return segment;
    }

    /// <summary>Single-stream of unknown size reached the end of the body: the file is exactly <paramref name="length"/> bytes.</summary>
    internal void FinishUnknownLength(long length)
    {
        lock (_segmentsLock)
        {
            _segments.Clear();
            _segments.Add(new Segment(0, length, length));
            State.TotalBytes = length;
        }
    }

    internal void Write(long offset, ReadOnlySpan<byte> data)
    {
        try
        {
            _part!.Write(offset, data);
        }
        catch (IOException ex)   // local disk, not the network: never retried as Transient
        {
            throw LocalFailure(ex);
        }
    }

    internal void AddDownloaded(int n) => Interlocked.Add(ref _downloaded, n);

    internal void ConnectionOpened() => Interlocked.Increment(ref _activeConnections);

    internal void ConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

    /// <summary>Ends the job with <paramref name="outcome"/> (first failure wins) and stops all workers.</summary>
    internal void Fail(JobOutcome outcome, Exception ex)
    {
        if (Interlocked.CompareExchange(ref _failure, outcome, null) is not null)
            return;
        _log.Error($"{State.FileName}: {outcome.Error}", ex);
        _stop?.Cancel();
    }

    /// <summary>
    /// Handles one failed request: fatal answers and failure streaks longer than GiveUpAfter end the job
    /// (<see cref="JobFailedException"/>); otherwise waits the backoff delay. Callers check their own
    /// cancellation before calling, so a pause never gets classified.
    /// </summary>
    internal async Task BackOffAsync(RetryStreak streak, Exception error, CancellationToken token)
    {
        HttpStatusCode? status = error is HttpStatusException h ? h.Status : null;
        var message = status is { } s ? $"Ошибка сервера: {(int)s}" : $"Ошибка загрузки: {error.Message}";
        if (RetryPolicy.Classify(status, status is null ? error : null) == ErrorKind.Fatal)
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, message), error);

        var failingFor = streak.Register();
        if (failingFor > Options.GiveUpAfter)
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, message), error);

        var delay = RetryPolicy.Delay(streak.Attempt, (error as HttpStatusException)?.RetryAfter);
        _log.Warn($"{State.FileName ?? State.CurrentUrl}: {error.Message}; retry in {delay.TotalSeconds:0.#} s");
        _statusText = $"Повтор через {Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))} с";
        try
        {
            await Task.Delay(delay, token);
        }
        finally
        {
            _statusText = null;
        }
    }

    // ---- state --------------------------------------------------------------------------------------------

    private bool AllDone()
    {
        lock (_segmentsLock)
        {
            foreach (var s in _segments)
            {
                if (!s.IsDone)
                    return false;
            }
            return true;
        }
    }

    private async Task SaveLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Options.StateSaveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                SaveState(DownloadStatus.Downloading, null);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Positions are captured first (under the segment-list lock), then the data is flushed, then the state with
    /// the captured positions is written — so the saved state is never ahead of the data on disk.
    /// </summary>
    private void SaveState(DownloadStatus status, string? error)
    {
        lock (_saveLock)
        {
            List<SegmentState> snapshot;
            lock (_segmentsLock)
            {
                snapshot = new List<SegmentState>(_segments.Count);
                foreach (var s in _segments)
                {
                    var (start, end, position) = s.Snapshot();
                    snapshot.Add(new SegmentState(start, end, position));
                }
            }

            State.Status = status;
            State.Error = error;
            try
            {
                _part?.FlushToDisk();
                State.Segments = snapshot;
                _store.Save(State);
            }
            catch (Exception ex)
            {
                _log.Warn($"state: save failed for {State.FileName ?? State.CurrentUrl}: {ex.Message}");
            }
        }
    }

    private JobOutcome Complete()
    {
        var part = _part!;
        _part = null;   // closed by Complete; no flush in the final save
        string final;
        try
        {
            final = part.Complete(Path.Combine(State.Folder, State.FileName!), IsTakenByOthers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            part.Dispose();
            _log.Error($"{State.FileName}: cannot finish the file", ex);
            return End(new JobOutcome(DownloadStatus.Error, $"Не удалось сохранить файл: {ex.Message}"));
        }

        State.FinalPath = final;
        State.FileName = Path.GetFileName(final);
        State.CompletedAt = DateTimeOffset.UtcNow;
        var outcome = new JobOutcome(DownloadStatus.Completed, null, final);
        End(outcome);
        _log.Info($"done {State.FileName}: {DownloadedBytes} bytes");
        return outcome;
    }

    /// <summary>Final save with the outcome's status (data flushed first while the part file is open).</summary>
    private JobOutcome End(JobOutcome outcome)
    {
        SaveState(outcome.Status, outcome.Error);
        return outcome;
    }

    private static JobFailedException LocalFailure(IOException ex) => ex is DiskFullException
        ? new JobFailedException(new JobOutcome(DownloadStatus.Paused, DiskFullText), ex)
        : new JobFailedException(new JobOutcome(DownloadStatus.Error, $"Ошибка записи на диск: {ex.Message}"), ex);

    // ---- paths --------------------------------------------------------------------------------------------

    /// <summary>The caller's isPathTaken, except for this job's own final and part paths.</summary>
    private bool IsTakenByOthers(string path) => !IsOwnPath(path) && _isPathTaken(path);

    private bool IsOwnPath(string path)
    {
        var full = Path.GetFullPath(path);
        return Same(full, State.FinalPath)
            || Same(full, State.PartPath)
            || (State.FileName is { } name && Same(full, Path.Combine(State.Folder, name)));
    }

    private static bool Same(string fullPath, string? other) =>
        other is not null && string.Equals(fullPath, Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);
}
