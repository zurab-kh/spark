using Strela.Engine.Manager;
using Strela.Engine.Storage;

namespace Strela.Engine.Jobs;

/// <summary>
/// Writes one job's <see cref="DownloadState"/>: segment positions are captured first, then the part file is
/// flushed, then the state with the captured positions is written — so the saved state is never ahead of the
/// data on disk. Saves are serialised by an internal lock, which also guards changes that reset the data
/// (<see cref="WithoutSaves"/>). Lock order: saver → whatever <c>snapshot</c> takes.
/// </summary>
internal sealed class JobStateSaver
{
    private readonly object _lock = new();
    private readonly DownloadState _state;
    private readonly StateStore _store;
    private readonly IStrelaLog _log;
    private readonly Func<List<SegmentState>?> _snapshot;
    private readonly Func<PartFile?> _part;

    /// <param name="snapshot">Current segment positions, or null to keep the saved ones (segments not loaded yet).</param>
    /// <param name="part">The open part file to flush before saving, or null.</param>
    public JobStateSaver(DownloadState state, StateStore store, IStrelaLog log,
                         Func<List<SegmentState>?> snapshot, Func<PartFile?> part)
    {
        _state = state;
        _store = store;
        _log = log;
        _snapshot = snapshot;
        _part = part;
    }

    /// <summary>Saves with <paramref name="status"/> and <paramref name="error"/>; a failed save is logged, never thrown.</summary>
    public void Save(DownloadStatus status, string? error)
    {
        lock (_lock)
        {
            var snapshot = _snapshot();
            _state.Status = status;
            _state.Error = error;
            try
            {
                _part()?.FlushToDisk();
                if (snapshot is not null)
                    _state.Segments = snapshot;
                _store.Save(_state);
            }
            catch (Exception ex)
            {
                _log.Warn($"state: save failed for {_state.FileName ?? _state.CurrentUrl}: {ex.Message}");
            }
        }
    }

    /// <summary>Runs <paramref name="change"/> (e.g. truncate the data and reset the segments) with no save in progress.</summary>
    public void WithoutSaves(Action change)
    {
        lock (_lock)
            change();
    }

    /// <summary>Saves as Downloading every <paramref name="interval"/> until <paramref name="token"/> is cancelled.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                Save(DownloadStatus.Downloading, null);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
