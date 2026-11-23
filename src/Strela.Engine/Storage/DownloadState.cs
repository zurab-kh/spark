using Strela.Engine.Manager;

namespace Strela.Engine.Storage;

/// <summary>Состояние одного сегмента: диапазон [Start, End] и позиция, до которой данные уже записаны.</summary>
public sealed record SegmentState(long Start, long End, long Position);

/// <summary>Всё, что нужно для возобновления загрузки после перезапуска. Сериализуется в JSON, поэтому свойства с get/set.</summary>
public sealed class DownloadState
{
    public Guid Id { get; set; }
    public string OriginalUrl { get; set; } = "";
    public string CurrentUrl { get; set; } = "";
    public string? Referer { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? Cookies { get; set; }
    public string? RequestedFileName { get; set; }

    public string Folder { get; set; } = "";
    public string? FileName { get; set; }
    public string? PartPath { get; set; }
    public string? FinalPath { get; set; }

    public long? TotalBytes { get; set; }
    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }

    public List<SegmentState> Segments { get; set; } = [];
    public DownloadStatus Status { get; set; }
    public string? Error { get; set; }

    public long QueueOrder { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public DownloadRequest ToRequest() =>
        new(new Uri(CurrentUrl), Referer, Headers, Cookies, RequestedFileName);
}
