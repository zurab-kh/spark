using System.Diagnostics;
using System.Net.NetworkInformation;
using Strela.Engine.Http;
using Strela.Engine.Jobs;
using Strela.Engine.Policies;
using Strela.Engine.Probe;
using Strela.Engine.Storage;

namespace Strela.Engine.Manager;

/// <summary>
/// The download queue: at most <see cref="EngineOptions.MaxActiveDownloads"/> jobs run at once, FIFO by QueueOrder.
/// Each run is a <see cref="DownloadJob"/> on the thread pool with its own <see cref="CancellationTokenSource"/>;
/// cancelling it pauses the job, which saves its own state.
/// <para>
/// Status: an entry's <c>Status</c> is what the user sees and wants. While a job is stopping, it already shows the
/// target (Paused, or Queued for a restart); when the run ends, <see cref="RunEntryAsync"/> reconciles the job's
/// outcome with it. A run that ends because the manager is shut down keeps Downloading in its state file, so the
/// next start continues it.
/// </para>
/// <para>
/// Locking: <c>_lock</c> guards the table and every entry's fields; it is never held across awaits or I/O.
/// While a job runs it owns <see cref="DownloadState"/> and its saves; the manager saves a state only while no job
/// runs, under the entry's <c>SaveLock</c> (taken before <c>_lock</c>), and a new run waits for that lock first.
/// Order: SaveLock → _lock → job internals.
/// </para>
/// </summary>
public sealed class DownloadManager : IDownloadManager
{
    private const string NetworkDownText = "Ожидание сети";
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly List<Entry> _list = [];                    // always in QueueOrder
    private readonly Dictionary<Guid, Entry> _byId = [];
    private readonly IStrelaLog _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly StateStore _store;
    private readonly HostLimits _limits = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private EngineOptions _options;
    private volatile bool _networkDown;
    private bool _disposed;

    public DownloadManager(EngineOptions options, IStrelaLog log, HttpClient? http = null)
    {
        _options = options;
        _log = log;
        _ownsHttp = http is null;
        _http = http ?? StrelaHttp.Create();
        _store = new StateStore(options.StateFolder);
        _networkDown = !NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event Action<DownloadSnapshot>? Completed;

    public bool HasActiveWork
    {
        get
        {
            lock (_lock)
                return _list.Exists(e => e.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        }
    }

    // ---- commands -----------------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        var states = await Task.Run(() => _store.LoadAll(_log)).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var state in states)
            {
                if (!IsUsable(state, out var why))
                {
                    _log.Warn($"state: skip {state.Id}: {why}");
                    continue;
                }
                if (_byId.ContainsKey(state.Id))
                    continue;

                var entry = new Entry(state, DisplayName(state));
                entry.Status = state.Status is DownloadStatus.Downloading or DownloadStatus.Queued
                    ? DownloadStatus.Queued   // was running or waiting when the app closed: continue
                    : state.Status;
                entry.StatusText = entry.Status is DownloadStatus.Error or DownloadStatus.Paused ? state.Error : null;
                entry.Downloaded = DownloadedOf(state);
                _byId.Add(state.Id, entry);
                _list.Add(entry);
            }

            _list.Sort((a, b) => a.State.QueueOrder != b.State.QueueOrder
                ? a.State.QueueOrder.CompareTo(b.State.QueueOrder)
                : a.State.AddedAt.CompareTo(b.State.AddedAt));
            _log.Info($"manager: {_list.Count} download(s) loaded");
        }

        Pump();
    }

    public Guid Add(DownloadRequest request)
    {
        var url = request.Url;
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"Not an http/https address: {url}", nameof(request));

        Entry entry;
        lock (_lock)
        {
            var key = url.AbsoluteUri;
            var existing = _list.Find(e =>
                (e.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused) &&
                (e.State.OriginalUrl == key || e.State.CurrentUrl == key));
            if (existing is not null)
                return existing.State.Id;

            var state = new DownloadState
            {
                Id = Guid.NewGuid(),
                OriginalUrl = key,
                CurrentUrl = key,
                Referer = request.Referer,
                Headers = request.Headers is null ? null : new Dictionary<string, string>(request.Headers),
                Cookies = request.Cookies,
                RequestedFileName = request.FileName,
                Folder = _options.DownloadFolder,
                Status = DownloadStatus.Queued,
                QueueOrder = _list.Count == 0 ? 0 : _list[^1].State.QueueOrder + 1,
                AddedAt = DateTimeOffset.UtcNow,
            };
            entry = new Entry(state, DisplayName(state)) { Status = DownloadStatus.Queued };
            _byId.Add(state.Id, entry);
            _list.Add(entry);
        }

        _log.Info($"add {entry.State.Id}: {url}");
        RequestSave(entry);
        Pump();
        return entry.State.Id;
    }

    public void Pause(Guid id)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out var e))
                PauseLocked(e);
        }
    }

    public void PauseAll()
    {
        lock (_lock)
        {
            foreach (var e in _list)
                PauseLocked(e);
        }
    }

    public void Resume(Guid id) => Requeue(id, DownloadStatus.Paused, DownloadStatus.Error);

    public void Retry(Guid id) => Requeue(id, DownloadStatus.Error, DownloadStatus.Paused);

    public void ResumeAll()
    {
        lock (_lock)
        {
            foreach (var e in _list)
            {
                if (e.Status == DownloadStatus.Paused)
                    RequeueLocked(e);
            }
        }

        Pump();
    }

    public async Task RemoveAsync(Guid id, bool deleteFile)
    {
        Entry? e;
        Task? run;
        lock (_lock)
        {
            if (!_byId.Remove(id, out e))
                return;
            _list.Remove(e);
            e.Removed = true;
            run = e.Run;
            Stop(e);
        }

        if (run is not null)
            await run.ConfigureAwait(false);   // the job has closed its part file

        await Task.Run(() =>
        {
            lock (e.SaveLock)
            {
                // No save is in flight and none will start (Removed).
                if (deleteFile)
                {
                    DeleteFile(e.State.PartPath);
                    DeleteFile(e.State.FinalPath);
                }
                try
                {
                    _store.Delete(id);
                }
                catch (Exception ex)
                {
                    _log.Warn($"state: cannot delete {id}: {ex.Message}");
                }
            }
        }).ConfigureAwait(false);

        _log.Info($"remove {id}{(deleteFile ? " with file" : "")}");
    }

    public async Task<bool> UpdateUrlAsync(Guid id, Uri newUrl)
    {
        if (!newUrl.IsAbsoluteUri || (newUrl.Scheme != Uri.UriSchemeHttp && newUrl.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"Not an http/https address: {newUrl}", nameof(newUrl));

        Entry? e;
        Task? run;
        EngineOptions options;
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out e) || e.Updating || e.Status == DownloadStatus.Completed)
                return false;
            e.Updating = true;   // the scheduler leaves it alone until the update is applied
            run = e.Run;
            if (e.Running && e.Status == DownloadStatus.Downloading)
                e.Status = DownloadStatus.Paused;
            Stop(e);
            options = _options;
        }

        try
        {
            if (run is not null)
                await run.ConfigureAwait(false);

            // Not running and not startable: the state is ours until Updating is cleared.
            var state = e.State;
            var probe = await ProbeAsync(state, newUrl, options).ConfigureAwait(false);
            var keep = probe is not null && probe.TotalBytes is { } size && size == state.TotalBytes && state.Segments.Count > 0;
            if (!keep && probe is not null)
            {
                // Another file (or an unknown size): nothing of the old part is reusable.
                if (state.PartPath is { } part)
                    await Task.Run(() => DeleteFile(part)).ConfigureAwait(false);
            }

            lock (_lock)
            {
                if (e.Removed)
                    return false;

                state.OriginalUrl = newUrl.AbsoluteUri;
                state.CurrentUrl = newUrl.AbsoluteUri;
                if (keep)
                {
                    // Same size counts as the same file: adopt the mirror's validators so the job continues.
                    state.SupportsRanges = probe!.SupportsRanges;
                    state.ETag = probe.ETag;
                    state.LastModified = probe.LastModified;
                }
                else if (probe is not null)
                {
                    state.TotalBytes = null;
                    state.SupportsRanges = false;
                    state.ETag = null;
                    state.LastModified = null;
                    state.Segments = [];
                    state.PartPath = null;
                    e.Downloaded = 0;
                }
                // probe failed: switch the address and keep the progress; the job verifies the file itself.

                e.Updating = false;
                RequeueLocked(e);
            }

            _log.Info($"update url {id}: {newUrl} ({(keep ? "progress kept" : "from zero")})");
            Pump();
            return keep;
        }
        finally
        {
            lock (_lock)
                e.Updating = false;
        }
    }

    public void Move(Guid id, int newIndex)
    {
        var changed = new List<Entry>();
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var e))
                return;
            _list.Remove(e);
            _list.Insert(Math.Clamp(newIndex, 0, _list.Count), e);
            for (var i = 0; i < _list.Count; i++)
            {
                if (_list[i].State.QueueOrder == i)
                    continue;
                _list[i].State.QueueOrder = i;
                changed.Add(_list[i]);
            }
        }

        foreach (var e in changed)
            RequestSave(e);
        Pump();
    }

    public void ApplyOptions(EngineOptions options)
    {
        lock (_lock)
        {
            _options = options;

            // A lower limit takes effect now: the latest running downloads go back to the queue.
            var running = 0;
            foreach (var e in _list)
            {
                if (!e.Running || e.Status != DownloadStatus.Downloading)
                    continue;
                if (++running > MaxActive)
                {
                    e.Status = DownloadStatus.Queued;
                    Stop(e);
                }
            }
        }

        Pump();
    }

    // ---- snapshots ----------------------------------------------------------------------------------------

    public IReadOnlyList<DownloadSnapshot> GetSnapshots()
    {
        lock (_lock)
        {
            var now = _clock.Elapsed;
            var result = new DownloadSnapshot[_list.Count];
            for (var i = 0; i < _list.Count; i++)
                result[i] = Snapshot(_list[i], now);
            return result;
        }
    }

    /// <summary>Under _lock. Samples the speed meter of a running download.</summary>
    private DownloadSnapshot Snapshot(Entry e, TimeSpan now)
    {
        var s = e.State;
        var downloaded = e.Downloaded;
        double speed = 0;
        TimeSpan? eta = null;
        var connections = 0;
        var text = e.StatusText;

        if (e.Job is { } job)
        {
            var bytes = job.DownloadedBytes;
            downloaded = bytes > 0 ? bytes : e.Downloaded;   // 0 until the job has loaded its segments
            connections = job.ActiveConnections;
            e.Speed.Sample(downloaded, now);
            if (e.Status == DownloadStatus.Downloading)
            {
                speed = Math.Max(0, e.Speed.BytesPerSecond);
                text = _networkDown ? NetworkDownText : job.StatusText;
                if (speed > 0 && s.TotalBytes is { } total && total >= downloaded)
                    eta = TimeSpan.FromSeconds((total - downloaded) / speed);
            }
        }

        return new DownloadSnapshot(s.Id, s.FileName ?? e.DisplayName, s.FinalPath ?? s.PartPath, s.OriginalUrl,
            s.TotalBytes, downloaded, speed, eta, e.Status, text, connections, s.QueueOrder);
    }

    // ---- scheduler ----------------------------------------------------------------------------------------

    private int MaxActive => Math.Max(1, _options.MaxActiveDownloads);

    /// <summary>Starts queued downloads in QueueOrder while slots are free. Safe to call from any thread, any time.</summary>
    private void Pump()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var running = 0;
            foreach (var e in _list)
            {
                if (e.Running)
                    running++;
            }

            foreach (var e in _list)
            {
                if (running >= MaxActive)
                    break;
                if (e.Status != DownloadStatus.Queued || e.Running || e.Updating)
                    continue;
                Start(e);
                running++;
            }
        }
    }

    /// <summary>Under _lock.</summary>
    private void Start(Entry e)
    {
        var cts = new CancellationTokenSource();
        var job = new DownloadJob(e.State, _options, _http, _limits, _store, _log, path => IsPathTaken(e, path));
        e.Job = job;
        e.Cts = cts;
        e.Running = true;
        e.Status = DownloadStatus.Downloading;
        e.StatusText = null;
        e.Speed = new SpeedMeter(SpeedWindow);
        e.Speed.Sample(e.Downloaded, _clock.Elapsed);
        e.Run = Task.Run(() => RunEntryAsync(e, job, cts.Token));
    }

    private async Task RunEntryAsync(Entry e, DownloadJob job, CancellationToken ct)
    {
        lock (e.SaveLock)
        {
            // A manager save that started before this run has finished; later ones see Running and skip.
        }

        JobOutcome outcome;
        try
        {
            outcome = await job.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)   // RunAsync reports failures as outcomes; this is a last line of defence
        {
            _log.Error($"{e.State.FileName ?? e.State.CurrentUrl}: run failed", ex);
            outcome = new JobOutcome(DownloadStatus.Error, $"Внутренняя ошибка: {ex.Message}");
        }

        DownloadSnapshot? completed = null;
        var save = false;
        lock (_lock)
        {
            e.Running = false;
            e.Job = null;
            e.Cts = null;
            e.Run = null;
            var state = e.State;
            e.Downloaded = outcome.Status == DownloadStatus.Completed
                ? state.TotalBytes ?? job.DownloadedBytes
                : DownloadedOf(state);

            if (!e.Removed)
            {
                switch (outcome.Status)
                {
                    case DownloadStatus.Completed:
                    case DownloadStatus.Error:
                        e.Status = outcome.Status;
                        e.StatusText = outcome.Error;
                        break;
                    case DownloadStatus.Paused when e.Status == DownloadStatus.Downloading:
                        // Stopped by shutdown: stays Downloading in the state file. Otherwise the job paused
                        // itself (no disk space).
                        if (!_disposed || outcome.Error is not null)
                        {
                            e.Status = DownloadStatus.Paused;
                            e.StatusText = outcome.Error;
                        }
                        break;
                    default:
                        // Paused by the user, or back in the queue (resumed while stopping, a restart).
                        e.StatusText = e.Status == DownloadStatus.Paused ? outcome.Error : null;
                        break;
                }

                if (e.Status == DownloadStatus.Completed)
                    completed = Snapshot(e, _clock.Elapsed);
                if (state.Status != e.Status)
                {
                    state.Status = e.Status;
                    state.Error = e.StatusText;
                    save = true;
                }
            }
        }

        if (save || Volatile.Read(ref e.SavePending) == 1)
            SaveNow(e);   // synchronous: DisposeAsync relies on it being done when this task ends
        Pump();

        if (completed is not null)
        {
            try
            {
                Completed?.Invoke(completed);
            }
            catch (Exception ex)
            {
                _log.Error("Completed handler failed", ex);
            }
        }
    }

    // ---- command helpers (under _lock) --------------------------------------------------------------------

    private void PauseLocked(Entry e)
    {
        if (e.Status is not (DownloadStatus.Downloading or DownloadStatus.Queued))
            return;
        e.Status = DownloadStatus.Paused;
        e.StatusText = null;
        if (e.Running)
        {
            Stop(e);   // the job saves Paused itself
        }
        else
        {
            e.State.Status = DownloadStatus.Paused;
            e.State.Error = null;
            RequestSave(e);
        }
    }

    private void Requeue(Guid id, params DownloadStatus[] from)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var e) || Array.IndexOf(from, e.Status) < 0)
                return;
            RequeueLocked(e);
        }

        Pump();
    }

    private void RequeueLocked(Entry e)
    {
        e.Status = DownloadStatus.Queued;
        e.StatusText = null;
        if (e.Running)
            return;   // still stopping: RunEntryAsync sees Queued and saves it
        e.State.Status = DownloadStatus.Queued;
        e.State.Error = null;
        RequestSave(e);
    }

    /// <summary>Cancels the entry's run, if any. Callbacks run on the pool, not under the caller's lock.</summary>
    private static void Stop(Entry e) => _ = e.Cts?.CancelAsync();

    // ---- paths --------------------------------------------------------------------------------------------

    /// <summary>
    /// For <paramref name="self"/>'s job: is <paramref name="path"/> (a candidate final path) the final or part path
    /// of another unfinished download? A free answer reserves the path for <paramref name="self"/>, so two jobs
    /// choosing names at the same moment never pick the same one.
    /// </summary>
    private bool IsPathTaken(Entry self, string path)
    {
        var full = Path.GetFullPath(path);
        var part = full + ".strela";
        lock (_lock)
        {
            foreach (var e in _list)
            {
                if (ReferenceEquals(e, self) || e.Status == DownloadStatus.Completed)
                    continue;
                var s = e.State;
                if (Same(full, s.FinalPath) || Same(full, s.PartPath) || Same(part, s.PartPath) || Same(full, e.ReservedPath))
                    return true;
            }

            self.ReservedPath = full;
            return false;
        }
    }

    private static bool Same(string fullPath, string? other) =>
        other is not null && string.Equals(fullPath, Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);

    private void DeleteFile(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log.Warn($"cannot delete {path}: {ex.Message}");
        }
    }

    // ---- persistence --------------------------------------------------------------------------------------

    /// <summary>
    /// Saves the entry's state on the pool soon (coalesced). While a job runs the request stays pending and is
    /// carried out when the run ends (the job's own saves may predate the change).
    /// </summary>
    private void RequestSave(Entry e)
    {
        if (Interlocked.Exchange(ref e.SavePending, 1) == 0)
            _ = Task.Run(() => Save(e, onlyIfPending: true));
    }

    private void SaveNow(Entry e) => Save(e, onlyIfPending: false);

    private void Save(Entry e, bool onlyIfPending)
    {
        lock (e.SaveLock)
        {
            lock (_lock)
            {
                if (e.Running)
                    return;   // the request stays pending: RunEntryAsync saves it when the run ends
                if (e.Removed)
                {
                    e.SavePending = 0;
                    return;
                }
            }
            if (Interlocked.Exchange(ref e.SavePending, 0) == 0 && onlyIfPending)
                return;

            try
            {
                _store.Save(e.State);
            }
            catch (Exception ex)
            {
                _log.Warn($"state: save failed for {e.State.FileName ?? e.State.CurrentUrl}: {ex.Message}");
            }
        }
    }

    // ---- network ------------------------------------------------------------------------------------------

    /// <summary>
    /// Network gone: Downloading rows say "Ожидание сети" (the jobs keep retrying). Network back: jobs waiting out a
    /// retry backoff are restarted right away instead of sleeping on.
    /// </summary>
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        _networkDown = !args.IsAvailable;
        if (_networkDown)
        {
            _log.Warn("network unavailable");
            return;
        }

        _log.Info("network available");
        lock (_lock)
        {
            if (_disposed)
                return;
            foreach (var e in _list)
            {
                if (e.Running && e.Status == DownloadStatus.Downloading && e.Job?.StatusText is not null)
                {
                    e.Status = DownloadStatus.Queued;   // RunEntryAsync keeps it queued, Pump starts it again
                    Stop(e);
                }
            }
        }
    }

    // ---- lifetime -----------------------------------------------------------------------------------------

    /// <summary>
    /// Stops every running job and waits for it; their state files keep Downloading / Queued, so the next start
    /// continues them. User-paused downloads stay Paused.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Task> runs = [];
        List<Entry> entries;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = [.. _list];
            foreach (var e in _list)
            {
                if (e.Run is { } run)
                {
                    runs.Add(run);
                    Stop(e);
                }
            }
        }

        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        await Task.WhenAll(runs).ConfigureAwait(false);

        foreach (var e in entries)
            Save(e, onlyIfPending: true);   // a requested save now; one in flight is waited out

        if (_ownsHttp)
            _http.Dispose();
        _log.Info("manager: stopped");
    }

    // ---- state helpers ------------------------------------------------------------------------------------

    /// <summary>LoadAll accepts any JSON object; only states a job can run are taken.</summary>
    private static bool IsUsable(DownloadState s, out string why)
    {
        why = s.Id == Guid.Empty ? "no id"
            : s.Segments is null ? "no segments"
            : !Uri.TryCreate(s.CurrentUrl, UriKind.Absolute, out var url)
              || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ? "bad url"
            : "";
        return why.Length == 0;
    }

    private static long DownloadedOf(DownloadState s)
    {
        if (s.Status == DownloadStatus.Completed)
            return s.TotalBytes ?? 0;
        long sum = 0;
        foreach (var seg in s.Segments)
            sum += seg.Position - seg.Start;
        return sum;
    }

    private static string DisplayName(DownloadState s)
    {
        if (!string.IsNullOrWhiteSpace(s.FileName))
            return s.FileName;
        if (!string.IsNullOrWhiteSpace(s.RequestedFileName))
            return FileNameResolver.Sanitize(s.RequestedFileName);
        return Uri.TryCreate(s.OriginalUrl, UriKind.Absolute, out var url) ? FileNameResolver.Resolve(null, url) : s.OriginalUrl;
    }

    /// <summary>Probes <paramref name="url"/> with the download's headers; null when it fails or answers an error.</summary>
    private async Task<ProbeResult?> ProbeAsync(DownloadState state, Uri url, EngineOptions options)
    {
        using var deadline = new CancellationTokenSource(options.StallTimeout);
        try
        {
            var request = new DownloadRequest(url, state.Referer, state.Headers, state.Cookies, state.RequestedFileName);
            var probe = await Prober.ProbeAsync(_http, request, deadline.Token).ConfigureAwait(false);
            if ((int)probe.Status < 400)
                return probe;
            _log.Warn($"update url: {url} answered {(int)probe.Status}");
        }
        catch (Exception ex)
        {
            _log.Warn($"update url: {url} failed: {ex.Message}");
        }
        return null;
    }

    private sealed class Entry(DownloadState state, string displayName)
    {
        public readonly object SaveLock = new();
        public int SavePending;                  // 1 = a manager save is requested; Interlocked

        public DownloadState State { get; } = state;
        public string DisplayName { get; } = displayName;

        // Guarded by the manager's _lock.
        public DownloadStatus Status;
        public string? StatusText;
        public long Downloaded;                  // progress while no job runs (from the saved segments)
        public bool Running;
        public bool Updating;
        public bool Removed;
        public DownloadJob? Job;
        public CancellationTokenSource? Cts;
        public Task? Run;
        public SpeedMeter Speed = new(SpeedWindow);
        public string? ReservedPath;
    }
}
