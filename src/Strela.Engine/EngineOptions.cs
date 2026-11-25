using System.Net;
using Strela.Engine.Video;

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

    // ---- torrents (read when the shared torrent engine starts: the first torrent of the session) ----

    /// <summary>TCP/UDP port for incoming peers and DHT. Busy → a random free port.</summary>
    public int TorrentPort { get; init; } = 51413;
    public int TorrentMaxConnectionsPerTorrent { get; init; } = 200;
    public bool TorrentDht { get; init; } = true;
    public bool TorrentLocalPeerDiscovery { get; init; } = true;
    /// <summary>UPnP / NAT-PMP port mapping.</summary>
    public bool TorrentPortForwarding { get; init; } = true;
    /// <summary>A magnet link that gets no metadata in this time ends in Error.</summary>
    public TimeSpan TorrentMetadataTimeout { get; init; } = TimeSpan.FromMinutes(5);

    // ---- video (YouTube through yt-dlp) ----

    public VideoQuality VideoQuality { get; init; } = VideoQuality.Best;
    /// <summary>yt-dlp, ffmpeg and deno, downloaded on the first YouTube link. null = "tools" in the state folder.</summary>
    public string? ToolsFolder { get; init; }

    /// <summary>Tests: use these tools, download nothing.</summary>
    internal VideoToolPaths? VideoToolsOverride { get; init; }
    /// <summary>Tests: runs yt-dlp instead of a real process.</summary>
    internal ToolRunner? VideoRunner { get; init; }

    /// <summary>Tests: listen on this address only (null = every interface, IPv4 and IPv6).</summary>
    internal IPAddress? TorrentListenAddress { get; init; }
    /// <summary>Tests: peers added by hand to every torrent (no trackers or DHT in a local swarm).</summary>
    internal IReadOnlyList<IPEndPoint> TorrentExtraPeers { get; init; } = [];
    /// <summary>Tests: upload limit of the whole torrent engine, bytes/s (0 = none).</summary>
    internal int TorrentMaxUploadRate { get; init; }
    /// <summary>Tests: download limit of the whole torrent engine, bytes/s (0 = none).</summary>
    internal int TorrentMaxDownloadRate { get; init; }
}
