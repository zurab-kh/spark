using System.Diagnostics;
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
/// Resilience: failed requests are retried with backoff, throttled connections leave (lowering the host's
/// ceiling), slow connections are reopened, a server that ignores Range sends the job back to one stream from 0,
/// a changed file restarts from 0, and only fatal answers or <see cref="EngineOptions.GiveUpAfter"/> without a
/// single committed byte end in Error.
/// </para>
/// <para>
/// Locking: <c>_segmentsLock</c> guards the segment list, segment ownership and the live-worker count; steals
/// (split + add) and the "all done" check happen under it, and so does the state snapshot, so a freshly stolen
/// tail is never missed. The <see cref="JobStateSaver"/> serialises state saves with single-stream truncation.
/// <c>_workersLock</c> guards the current phase's worker list (which auto mode grows while it runs).
/// Order: saver → _workersLock → _segmentsLock → Segment.
/// </para>
/// <para>
/// Auto mode (<see cref="EngineOptions.ConnectionsPerFile"/> = 0): start with 8 connections; every
/// <see cref="EngineOptions.AutoTuneInterval"/> add 4 more (new workers steal tails) while at least 8 MB remain and
/// the last step raised throughput by ≥ 10%; once a step does not pay off the count is frozen. Never above 32 or the
/// host's ceiling.
/// </para>
/// </summary>
internal sealed class DownloadJob
{
    private const string DiskFullText = "Нет места на диске";
    private const string StaleLinkText = "Ссылка устарела — обновите ссылку";
    private const int AutoStartConnections = 8;
    private const int AutoStepConnections = 4;
    private const int AutoMaxConnections = 32;
    private const long AutoMinRemainingBytes = 8L << 20;
    private const double AutoMinGrowth = 1.10;
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly HostLimits _limits;
    private readonly IStrelaLog _log;
    private readonly JobStateSaver _saver;
    private readonly Func<string, bool> _isPathTaken;

    private readonly object _segmentsLock = new();
    private readonly List<Segment> _segments = [];
    private readonly HashSet<Segment> _owned = [];
    private readonly List<long> _retryDeadlines = [];   // TickCount64 of every pending retry; guarded by itself

    private PartFile? _part;
    private CancellationTokenSource? _stop;   // the current worker phase
    private JobOutcome? _failure;
    private long _downloaded;
    private long _failingSinceMs = -1;        // first failure since the last committed byte; -1 = none
    private int _activeConnections;
    private int _receiving;                   // connections receiving a body right now
    private int _rangesIgnored;               // 1 = a ranged request got the whole file: rerun as single-stream
    private int _liveWorkers;                 // guarded by _segmentsLock
    private bool _segmentsLoaded;             // guarded by _segmentsLock; false until Prepare filled _segments
    private int _peakWorkers;

    private readonly object _workersLock = new();
    private readonly List<SegmentWorker> _workers = [];   // the current phase; guarded by _workersLock
    private readonly List<Task> _workerTasks = [];        // guarded by _workersLock
    private bool _workersClosed;                          // guarded by _workersLock: the phase ended, add no more

    public DownloadJob(DownloadState state, EngineOptions options, HttpClient http, HostLimits limits,
                       StateStore store, IStrelaLog log, Func<string, bool> isPathTaken)
    {
        State = state;
        Options = options;
        _http = http;
        _limits = limits;
        _log = log;
        _isPathTaken = isPathTaken;
        _saver = new JobStateSaver(state, store, log, SnapshotSegments, () => _part);
    }

    public DownloadState State { get; }

    /// <summary>Sum over segments of (Position − Start).</summary>
    public long DownloadedBytes => Interlocked.Read(ref _downloaded);

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>"Повтор через N с" (the soonest pending retry) while any connection waits to retry; null otherwise.</summary>
    public string? StatusText
    {
        get
        {
            lock (_retryDeadlines)
            {
                if (_retryDeadlines.Count == 0)
                    return null;
                var ms = _retryDeadlines.Min() - Environment.TickCount64;
                return $"Повтор через {Math.Max(1, (int)Math.Ceiling(ms / 1000.0))} с";
            }
        }
    }

    internal EngineOptions Options { get; }
    internal HttpClient Http => _http;
    internal DownloadRequest Request { get; private set; } = null!;
    internal Uri Uri { get; private set; } = null!;
    internal bool SingleStream { get; private set; }
    internal bool KnownLength => State.TotalBytes is not null;

    /// <summary>Auto mode: the number of connections is tuned while downloading.</summary>
    internal bool AutoConnections => Options.ConnectionsPerFile <= 0;

    /// <summary>The most workers that were live at once in this job (for tests and diagnostics).</summary>
    internal int PeakWorkers => Volatile.Read(ref _peakWorkers);

    private int RequestedConnections => AutoConnections ? AutoMaxConnections : Options.ConnectionsPerFile;

    public async Task<JobOutcome> RunAsync(CancellationToken ct)
    {
        State.Status = DownloadStatus.Downloading;
        State.Error = null;
        _failure = null;
        Interlocked.Exchange(ref _failingSinceMs, -1);
        Interlocked.Exchange(ref _rangesIgnored, 0);
        lock (_segmentsLock)
            _segmentsLoaded = false;
        try
        {
            var probe = await ProbeAsync(ct);
            var restart = Prepare(probe);
            OpenPartFile(restart);
            return End(await TransferAsync(ct));
        }
        catch (JobFailedException ex) when (!PausedOver(ex.Outcome, ct))
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

    /// <summary>Fills the live segments from the probe and the saved state. Returns true when the part file must start empty.</summary>
    private bool Prepare(ProbeResult probe)
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
            throw FolderFailure(ex);
        }

        var restart = false;
        if (State.TotalBytes is null && State.Segments.Count == 0)
        {
            Adopt(probe);
            var final = FileNameResolver.ReserveUniquePath(State.Folder, probe.FileName, IsTakenByOthers);
            State.FileName = Path.GetFileName(final);
            State.PartPath = final + ".strela";
        }
        else if (FileChanged(probe))
        {
            _log.Warn($"restart: file changed: {State.FileName} (size {State.TotalBytes} → {probe.TotalBytes}, " +
                      $"ETag {State.ETag} → {probe.ETag}, Last-Modified {State.LastModified:R} → {probe.LastModified:R})");
            Adopt(probe);
            State.Segments = [];   // the new file's metadata is never saved next to the old file's progress
            restart = true;
        }
        else if (!probe.SupportsRanges)
        {
            State.SupportsRanges = false;
        }

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
        else if (State.Segments.Count > 0 && File.Exists(State.PartPath) && Tiles(State.Segments, State.TotalBytes!.Value))
        {
            foreach (var s in State.Segments)
                segments.Add(new Segment(s.Start, s.End, s.Position));
        }
        else
        {
            if (State.Segments.Count > 0)
            {
                _log.Warn(File.Exists(State.PartPath)
                    ? $"{State.FileName}: saved segments do not cover the file, downloading it again"
                    : $"{State.FileName}: part file is missing, starting over");
            }
            segments = SegmentPlanner.InitialSplit(State.TotalBytes!.Value, StartConnections(), Options.MinSegmentBytes);
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
            _segmentsLoaded = true;
        }

        return restart;
    }

    private void Adopt(ProbeResult probe)
    {
        State.TotalBytes = probe.TotalBytes;
        State.SupportsRanges = probe.SupportsRanges;
        State.ETag = probe.ETag;
        State.LastModified = probe.LastModified;
    }

    /// <summary>Size differs, or the ETag (when both are known; otherwise Last-Modified) differs.</summary>
    private bool FileChanged(ProbeResult probe)
    {
        if (probe.TotalBytes != State.TotalBytes)
            return true;
        if (probe.ETag is not null && State.ETag is not null)
            return probe.ETag != State.ETag;
        if (probe.LastModified is { } now && State.LastModified is { } then)
            return now != then;
        return false;
    }

    /// <summary>Saved segments cover [0, total) exactly, each with Start ≤ Position ≤ End.</summary>
    private static bool Tiles(List<SegmentState> saved, long total)
    {
        long cursor = 0;
        foreach (var s in saved.OrderBy(s => s.Start))
        {
            if (s.Start != cursor || s.Position < s.Start || s.Position > s.End)
                return false;
            cursor = s.End;
        }
        return cursor == total;
    }

    private void OpenPartFile(bool restart)
    {
        try
        {
            if (restart)
                PartFile.Delete(State.PartPath!);   // nothing of the old file may survive
            _part = PartFile.OpenOrCreate(State.PartPath!, SingleStream ? null : State.TotalBytes);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw FolderFailure(ex);
        }
        catch (IOException ex)
        {
            throw LocalFailure(ex);
        }
    }

    private int ConnectionCeiling() => Math.Max(1, _limits.Ceiling(Uri.Host, RequestedConnections));

    /// <summary>Connections a phase starts with: the ceiling, or in auto mode at most <see cref="AutoStartConnections"/>.</summary>
    private int StartConnections() =>
        AutoConnections ? Math.Min(AutoStartConnections, ConnectionCeiling()) : ConnectionCeiling();

    /// <summary>A pause that raced with an error wins: pausing never ends a download in Error.</summary>
    private static bool PausedOver(JobOutcome outcome, CancellationToken ct) =>
        ct.IsCancellationRequested && outcome.Status == DownloadStatus.Error;

    // ---- transfer -----------------------------------------------------------------------------------------

    private async Task<JobOutcome> TransferAsync(CancellationToken ct)
    {
        using var saverStop = new CancellationTokenSource();
        var saver = _saver.RunAsync(Options.StateSaveInterval, saverStop.Token);
        try
        {
            while (true)
            {
                await RunWorkersAsync(ct);
                if (Volatile.Read(ref _failure) is not null || ct.IsCancellationRequested
                    || Interlocked.Exchange(ref _rangesIgnored, 0) == 0)
                    break;

                // A ranged request got the whole file: everything again over one stream from byte 0.
                State.SupportsRanges = false;
                SingleStream = true;
            }
        }
        finally
        {
            saverStop.Cancel();
            await saver;
        }

        if (Volatile.Read(ref _failure) is { } failure && !PausedOver(failure, ct))
            return failure;
        if (AllDone())
            return Complete();
        if (ct.IsCancellationRequested)
            return new JobOutcome(DownloadStatus.Paused);

        _log.Error($"{State.FileName}: workers stopped with unfinished segments");
        return new JobOutcome(DownloadStatus.Error, "Внутренняя ошибка: загрузка остановилась");
    }

    /// <summary>One phase: workers plus the slow-connection watch, until every worker has ended.</summary>
    private async Task RunWorkersAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var auto = AutoConnections && !SingleStream;
        int count;
        lock (_segmentsLock)
        {
            count = SingleStream ? 1 : Math.Max(1, Math.Min(StartConnections(), _segments.Count));
            _liveWorkers = count;
        }
        _log.Info($"start {State.FileName}: {count} connection(s), {State.TotalBytes?.ToString() ?? "?"} bytes");
        if (auto)
            _log.Info($"auto: {count} connections");

        lock (_workersLock)
        {
            _workers.Clear();
            _workerTasks.Clear();
            _workersClosed = false;
            StartWorkers(count, stop.Token);
        }
        NotePeak(count);
        _stop = stop;

        using var watchStop = new CancellationTokenSource();
        var watch = WatchSlowAsync(mayGrow: auto, watchStop.Token);
        var tune = auto ? AutoTuneAsync(stop.Token, watchStop.Token) : Task.CompletedTask;
        try
        {
            // Auto mode may add workers while we wait: wait again until no new ones appeared, then close the phase.
            while (true)
            {
                Task[] running;
                lock (_workersLock)
                    running = [.. _workerTasks];
                await Task.WhenAll(running);   // workers never throw
                lock (_workersLock)
                {
                    if (_workerTasks.Count == running.Length)
                    {
                        _workersClosed = true;
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_workersLock)
                _workersClosed = true;
            watchStop.Cancel();
            await watch;
            await tune;
            _stop = null;
        }
    }

    /// <summary>Creates and starts <paramref name="count"/> workers. Caller holds _workersLock and has counted them live.</summary>
    private void StartWorkers(int count, CancellationToken token)
    {
        for (var i = 0; i < count; i++)
        {
            var worker = new SegmentWorker(this, _workers.Count, token);
            _workers.Add(worker);
            _workerTasks.Add(Task.Run(worker.RunAsync, CancellationToken.None));
        }
    }

    private void NotePeak(int live)
    {
        int seen;
        while (live > (seen = Volatile.Read(ref _peakWorkers))
               && Interlocked.CompareExchange(ref _peakWorkers, live, seen) != seen)
        {
        }
    }

    /// <summary>
    /// Auto mode: every AutoTuneInterval measures the job's throughput over the interval. The first step always adds
    /// connections; later steps only while the previous one raised throughput by ≥ 10% over the interval before it.
    /// Stops for good (frozen) when a step did not pay off, fewer than 8 MB remain, or no connection can be added
    /// (32, or the host's ceiling after 429/503).
    /// </summary>
    private async Task AutoTuneAsync(CancellationToken workerToken, CancellationToken token)
    {
        using var timer = new PeriodicTimer(Options.AutoTuneInterval);
        var clock = Stopwatch.StartNew();
        var lastBytes = ReceivedBytes();
        var lastTime = TimeSpan.Zero;
        double? beforeStep = null;   // throughput of the interval before the last step
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var now = clock.Elapsed;
                var bytes = ReceivedBytes();
                var rate = (bytes - lastBytes) / Math.Max(0.001, (now - lastTime).TotalSeconds);
                lastBytes = bytes;
                lastTime = now;

                if (beforeStep is { } before && rate < before * AutoMinGrowth)
                {
                    _log.Info($"auto: {State.FileName}: frozen, {rate / 1024:0} KB/s after the step vs {before / 1024:0} KB/s before");
                    return;
                }
                if (State.TotalBytes is not { } total || total - DownloadedBytes < AutoMinRemainingBytes)
                    return;   // the tail: more connections would only split crumbs
                if (AddWorkers(AutoStepConnections, workerToken) == 0)
                    return;   // at 32 or the host's ceiling
                beforeStep = rate;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Bytes read from the network by this phase's workers. Throughput is measured on these, not on committed
    /// bytes: those arrive in BufferBytes blocks, far too coarse for a short interval.
    /// </summary>
    private long ReceivedBytes()
    {
        lock (_workersLock)
        {
            long sum = 0;
            foreach (var w in _workers)
                sum += w.ReceivedBytes;
            return sum;
        }
    }

    /// <summary>Adds up to <paramref name="step"/> workers within the ceiling; returns how many were added.</summary>
    private int AddWorkers(int step, CancellationToken token)
    {
        int added, live;
        lock (_workersLock)
        {
            if (_workersClosed || token.IsCancellationRequested)
                return 0;
            lock (_segmentsLock)
            {
                // Nobody live: everything is done or reserved, there is nothing to share.
                added = _liveWorkers == 0 ? 0 : Math.Clamp(ConnectionCeiling() - _liveWorkers, 0, step);
                _liveWorkers += added;
                live = _liveWorkers;
            }
            StartWorkers(added, token);
        }

        if (added > 0)
        {
            NotePeak(live);
            _log.Info($"auto: {live} connections");
        }
        return added;
    }

    /// <summary>
    /// Every SlowCheckInterval: each open connection's speed and remaining bytes go to the
    /// <see cref="SlowConnectionMonitor"/>; flagged connections are reopened from their segment's Position.
    /// </summary>
    private async Task WatchSlowAsync(bool mayGrow, CancellationToken token)
    {
        lock (_workersLock)
        {
            if (_workers.Count < 2 && !mayGrow)
                return;
        }
        var monitor = new SlowConnectionMonitor(Options.SlowThreshold, Options.SlowGrace, 2 * Options.MinSegmentBytes);
        var meters = new List<SpeedMeter>();
        var samples = new List<WorkerSample>();
        var clock = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(Options.SlowCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                SegmentWorker[] workers;
                lock (_workersLock)
                    workers = [.. _workers];
                while (meters.Count < workers.Length)
                    meters.Add(new SpeedMeter(SpeedWindow));

                var now = clock.Elapsed;
                samples.Clear();
                foreach (var w in workers)
                {
                    meters[w.Id].Sample(w.ReceivedBytes, now);
                    if (w.OpenRequestRemaining is { } remaining)
                        samples.Add(new WorkerSample(w.Id, meters[w.Id].BytesPerSecond, remaining));
                }

                foreach (var id in monitor.Evaluate(samples, now))
                {
                    var speed = meters[id].BytesPerSecond;
                    if (!workers[id].ReplaceConnection())
                        continue;
                    _log.Info($"replace slow: {State.FileName} connection {id} at {speed / 1024:0} KB/s");
                    meters[id] = new SpeedMeter(SpeedWindow);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Next segment for a worker that finished <paramref name="finished"/> (or is just starting): an unfinished
    /// segment nobody owns, else a tail stolen from the segment with the most unreserved bytes; null = no work,
    /// and the worker ends.
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
            {
                _liveWorkers--;
                return null;
            }
            _segments.Add(tail);
            _owned.Add(tail);
            stolen = tail;
        }

        _log.Info($"split: [{stolen.Start}, {stolen.End})");
        return stolen;
    }

    /// <summary>
    /// A worker got 429/503 for <paramref name="segment"/>: unless it is the last live worker it leaves, its segment
    /// goes back to the pool for the others (who pick it up in <see cref="TakeWork"/> before they can end), and the
    /// host's ceiling drops to the connections receiving data right now. False = stay and back off.
    /// </summary>
    internal bool TryLeaveThrottled(int workerId, Segment segment)
    {
        int live;
        lock (_segmentsLock)
        {
            if (_liveWorkers <= 1)
                return false;
            live = --_liveWorkers;
            _owned.Remove(segment);
        }

        var working = Volatile.Read(ref _receiving);
        _limits.Lower(Uri.Host, working);
        _log.Warn($"throttle: {State.FileName}: connection {workerId} leaves, {working} receiving, " +
                  $"ceiling for {Uri.Host}: {_limits.Ceiling(Uri.Host, RequestedConnections)}");
        if (AutoConnections)
            _log.Info($"auto: {live} connections");
        return true;
    }

    /// <summary>
    /// The server answered a ranged request at <paramref name="offset"/> &gt; 0 with the whole file (200): nothing of it
    /// is written; all workers stop and the job reruns as one stream from byte 0.
    /// </summary>
    internal void RangesIgnored(long offset)
    {
        if (Interlocked.Exchange(ref _rangesIgnored, 1) == 1)
            return;
        _log.Warn($"single-stream: {State.FileName}: server ignored Range at offset {offset}, restarting from 0");
        _stop?.Cancel();
    }

    /// <summary>Single-stream (re)start: empty part file, one segment from 0, zero progress.</summary>
    internal Segment RestartSingleStream()
    {
        var segment = new Segment(0, State.TotalBytes ?? long.MaxValue, 0);
        _saver.WithoutSaves(() =>
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
                _owned.Clear();
                Interlocked.Exchange(ref _downloaded, 0);
            }
        });

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

    /// <summary>Committed bytes; any progress ends the job-wide failure streak.</summary>
    internal void AddDownloaded(int n)
    {
        Interlocked.Add(ref _downloaded, n);
        if (Volatile.Read(ref _failingSinceMs) >= 0)
            Interlocked.Exchange(ref _failingSinceMs, -1);
    }

    internal void ConnectionOpened() => Interlocked.Increment(ref _activeConnections);

    internal void ConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

    internal void ResponseStarted() => Interlocked.Increment(ref _receiving);

    internal void ResponseEnded() => Interlocked.Decrement(ref _receiving);

    /// <summary>Ends the job with <paramref name="outcome"/> (first failure wins) and stops all workers.</summary>
    internal void Fail(JobOutcome outcome, Exception ex)
    {
        if (Interlocked.CompareExchange(ref _failure, outcome, null) is not null)
            return;
        _log.Error($"{State.FileName}: {outcome.Error}", ex);
        _stop?.Cancel();
    }

    /// <summary>
    /// Handles one failed request: a fatal answer, or <see cref="EngineOptions.GiveUpAfter"/> without a committed
    /// byte anywhere in the job, ends the job (<see cref="JobFailedException"/>); otherwise waits the backoff delay
    /// (never past the give-up deadline, so the last attempt happens right at it). Callers check their own
    /// cancellation before calling, so a pause never gets classified.
    /// </summary>
    internal async Task BackOffAsync(RetryStreak streak, Exception error, CancellationToken token)
    {
        var http = error as HttpStatusException;
        if (RetryPolicy.Classify(http?.Status, http is null ? error : null) == ErrorKind.Fatal)
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, FatalText(http?.Status, error)), error);

        var failingFor = FailingFor();
        if (failingFor >= Options.GiveUpAfter)
            throw new JobFailedException(new JobOutcome(DownloadStatus.Error, $"Сервер недоступен: {error.Message}"), error);

        var delay = RetryPolicy.Delay(streak.Next(), http?.RetryAfter);
        var left = Options.GiveUpAfter - failingFor;
        if (delay > left)
            delay = left;
        _log.Warn($"{State.FileName ?? State.CurrentUrl}: {error.Message}; retry in {delay.TotalSeconds:0.#} s");

        var due = Environment.TickCount64 + (long)delay.TotalMilliseconds;
        lock (_retryDeadlines)
            _retryDeadlines.Add(due);
        try
        {
            await Task.Delay(delay, token);
        }
        finally
        {
            lock (_retryDeadlines)
                _retryDeadlines.Remove(due);
        }
    }

    /// <summary>How long the job has gone without a committed byte since its first failure (starts the clock).</summary>
    private TimeSpan FailingFor()
    {
        var now = Environment.TickCount64;
        var since = Interlocked.CompareExchange(ref _failingSinceMs, now, -1);
        return TimeSpan.FromMilliseconds(since < 0 ? 0 : now - since);
    }

    private string FatalText(HttpStatusCode? status, Exception error) => (int?)status switch
    {
        404 or 410 => $"Файл не найден на сервере ({(int)status!.Value})",
        403 => HasProgress() ? StaleLinkText : "Доступ запрещён (403)",
        { } code => $"Ошибка сервера: {code}",
        null => $"Ошибка загрузки: {error.Message}",
    };

    /// <summary>Something of this download is already on disk (this run, an earlier run, or the saved state).</summary>
    private bool HasProgress() => DownloadedBytes > 0 || State.Segments.Exists(s => s.Position > s.Start);

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

    /// <summary>
    /// Segment positions for the saver, taken under the segment-list lock. Null when this run has not loaded the
    /// segments yet (pause or failure while probing): an empty live list must never overwrite real saved progress.
    /// </summary>
    private List<SegmentState>? SnapshotSegments()
    {
        lock (_segmentsLock)
        {
            if (!_segmentsLoaded)
                return null;
            var snapshot = new List<SegmentState>(_segments.Count);
            foreach (var s in _segments)
            {
                var (start, end, position) = s.Snapshot();
                snapshot.Add(new SegmentState(start, end, position));
            }
            return snapshot;
        }
    }

    /// <summary>Renames the part file to the final name. The outcome is saved once, by <see cref="RunAsync"/>.</summary>
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
            _log.Error($"{State.FileName}: cannot finish the file", ex);
            return new JobOutcome(DownloadStatus.Error, $"Не удалось сохранить файл: {ex.Message}");
        }
        finally
        {
            part.Dispose();   // no-op after a successful Complete
        }

        State.FinalPath = final;
        State.FileName = Path.GetFileName(final);
        State.CompletedAt = DateTimeOffset.UtcNow;
        _log.Info($"done {State.FileName}: {DownloadedBytes} bytes");
        return new JobOutcome(DownloadStatus.Completed, null, final);
    }

    /// <summary>Final save with the outcome's status (data flushed first while the part file is open).</summary>
    private JobOutcome End(JobOutcome outcome)
    {
        _saver.Save(outcome.Status, outcome.Error);
        return outcome;
    }

    private JobFailedException FolderFailure(Exception ex) =>
        new(new JobOutcome(DownloadStatus.Error, $"Нет доступа к папке: {State.Folder}"), ex);

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
