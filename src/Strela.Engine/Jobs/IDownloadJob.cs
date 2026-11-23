using Strela.Engine.Storage;

namespace Strela.Engine.Jobs;

/// <summary>What the manager needs from a running download: an HTTP <see cref="DownloadJob"/> or a torrent.</summary>
internal interface IDownloadJob
{
    DownloadState State { get; }

    /// <summary>Bytes of the file(s) downloaded so far.</summary>
    long DownloadedBytes { get; }

    int ActiveConnections { get; }

    /// <summary>Extra status for the row (a retry countdown, seeds and peers…), or null.</summary>
    string? StatusText { get; }

    /// <summary>The job waits out a retry backoff: restart it right away when the network comes back.</summary>
    bool WaitingToRetry { get; }

    /// <summary>Runs until done, failed or cancelled (cancel = pause; the job saves its state).</summary>
    Task<JobOutcome> RunAsync(CancellationToken ct);
}
