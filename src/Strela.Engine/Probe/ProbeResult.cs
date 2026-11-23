using System.Net;

namespace Strela.Engine.Probe;

/// <summary>Итог разведки: что известно о файле до начала загрузки. TotalBytes = null — размер неизвестен.</summary>
public sealed record ProbeResult(
    Uri FinalUri,
    long? TotalBytes,
    bool SupportsRanges,
    string? ETag,
    DateTimeOffset? LastModified,
    string FileName,
    HttpStatusCode Status);
