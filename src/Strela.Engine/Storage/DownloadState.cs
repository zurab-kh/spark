using Strela.Engine.Manager;

namespace Strela.Engine.Storage;

/// <summary>Состояние одного сегмента: диапазон [Start, End) (End не включается, как у Segment) и позиция, до которой данные уже записаны.</summary>
public sealed record SegmentState(long Start, long End, long Position);

/// <summary>Всё, что нужно для возобновления загрузки после перезапуска. Сериализуется в JSON, поэтому свойства с get/set.</summary>
public sealed class DownloadState
{
    public Guid Id { get; set; }
    /// <summary>Нет в старых файлах → Http.</summary>
    public DownloadKind Kind { get; set; } = DownloadKind.Http;
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

    // ---- торренты (Kind = Torrent); OriginalUrl — то, что добавил пользователь: magnet, путь к .torrent или http(s)-ссылка ----

    /// <summary>magnet-ссылка, если торрент добавлен ею.</summary>
    public string? Magnet { get; set; }
    /// <summary>Копия .torrent в папке состояния (после разбора файла, скачивания по ссылке или получения метаданных).</summary>
    public string? TorrentFilePath { get; set; }
    /// <summary>Hex info-hash (v1, а для чистого v2 — v2); по нему ищутся дубликаты.</summary>
    public string? InfoHash { get; set; }
    /// <summary>Скачано байт (проверенные куски) на момент последнего сохранения.</summary>
    public long BytesDone { get; set; }

    public DownloadRequest ToRequest() =>
        new(new Uri(CurrentUrl), Referer, Headers, Cookies, RequestedFileName);
}
