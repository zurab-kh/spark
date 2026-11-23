using Strela.Engine;
using Strela.Engine.Manager;

namespace Strela.App.Tests;

public sealed class FakeDownloadManager : IDownloadManager
{
    public List<DownloadSnapshot> Snapshots { get; } = new();
    public List<DownloadRequest> Added { get; } = new();
    public List<(Guid Id, int Index)> Moves { get; } = new();
    public List<(Guid Id, bool DeleteFile)> Removed { get; } = new();
    public List<(Guid Id, Uri Url)> UrlUpdates { get; } = new();
    public bool HasActiveWork { get; set; }

    public event Action<DownloadSnapshot>? Completed;
    public void RaiseCompleted(DownloadSnapshot s) => Completed?.Invoke(s);

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>When set, Add returns this id — imitates the manager returning an existing download for a duplicate link.</summary>
    public Guid? AddReturns { get; set; }

    public Guid Add(DownloadRequest request)
    {
        Added.Add(request);
        return AddReturns ?? Guid.NewGuid();
    }

    public void Pause(Guid id) { }
    public void Resume(Guid id) { }
    public void Retry(Guid id) { }
    public void PauseAll() { }
    public void ResumeAll() { }

    public Task RemoveAsync(Guid id, bool deleteFile)
    {
        Removed.Add((id, deleteFile));
        Snapshots.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }

    public Task<bool> UpdateUrlAsync(Guid id, Uri newUrl)
    {
        UrlUpdates.Add((id, newUrl));
        return Task.FromResult(true);
    }

    public void Move(Guid id, int newIndex) => Moves.Add((id, newIndex));
    public void ApplyOptions(EngineOptions options) { }
    public IReadOnlyList<DownloadSnapshot> GetSnapshots() => Snapshots.OrderBy(s => s.QueueOrder).ToList();
    public ValueTask DisposeAsync() => default;

    public static DownloadSnapshot Snap(Guid id, long order, DownloadStatus status = DownloadStatus.Downloading,
        long downloaded = 0, long? total = 100, double bps = 0, string name = "f.bin", string? statusText = null)
        => new(id, name, null, "https://h/" + name, total, downloaded, bps, null, status, statusText, 1, order);
}
