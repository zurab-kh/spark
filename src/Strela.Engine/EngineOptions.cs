namespace Strela.Engine;

public sealed record EngineOptions
{
    public required string DownloadFolder { get; init; }
    public required string StateFolder { get; init; }
    public int ConnectionsPerFile { get; init; } = 16;
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
