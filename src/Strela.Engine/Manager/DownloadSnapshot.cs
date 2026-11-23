namespace Strela.Engine.Manager;

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Error }

public enum DownloadKind { Http, Torrent }

public sealed record DownloadSnapshot(Guid Id, string FileName, string? FilePath, string Url,
    long? TotalBytes, long DownloadedBytes, double BytesPerSecond, TimeSpan? Eta,
    DownloadStatus Status, string? StatusText, int Connections, long QueueOrder,
    DownloadKind Kind = DownloadKind.Http);
