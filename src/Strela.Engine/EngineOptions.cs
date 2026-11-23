namespace Strela.Engine;

public sealed record EngineOptions
{
    public required string DownloadFolder { get; init; }
    public required string StateFolder { get; init; }
    /// <summary>Connections per file: 1..32 fixed, 0 = auto (start with 8, grow by 4 while it pays off, up to 32).</summary>
    public int ConnectionsPerFile { get; init; } = 0;
    /// <summary>Auto mode: how often throughput is measured and connections may be added.</summary>
    public TimeSpan AutoTuneInterval { get; init; } = TimeSpan.FromSeconds(3);
    public int MaxActiveDownloads { get; init; } = 3;
    public long MinSegmentBytes { get; init; } = 1 << 20;
    public int BufferBytes { get; init; } = 512 * 1024;
    public TimeSpan StateSaveInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SlowCheckInterval { get; init; } = TimeSpan.FromSeconds(2);
    public double SlowThreshold { get; init; } = 0.25;
    public TimeSpan SlowGrace { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);   // нет байтов → соединение мёртвое
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(2);
}
