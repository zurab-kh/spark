using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace Strela.Engine.Torrents;

/// <summary>
/// The one MonoTorrent <see cref="ClientEngine"/> of the app, created on the first torrent (no listening port and
/// no DHT traffic for people who never download torrents). Fast-resume data, magnet metadata and the DHT node cache
/// live under <c>StateFolder/torrents</c>.
/// </summary>
internal sealed class TorrentService : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly IStrelaLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EngineOptions _options;
    private ClientEngine? _engine;
    private bool _disposed;

    public TorrentService(EngineOptions options, IStrelaLog log)
    {
        _options = options;
        _log = log;
        CacheFolder = Path.Combine(options.StateFolder, "torrents");
    }

    /// <summary>Engine cache (fast resume, magnet metadata, DHT nodes) and our copies of .torrent files.</summary>
    public string CacheFolder { get; }

    /// <summary>The port the engine listens on, once it runs.</summary>
    public int ListenPort { get; private set; }

    /// <summary>Torrents loaded in the engine right now; for tests.</summary>
    public int LoadedTorrents => _engine?.Torrents.Count ?? 0;

    public string TorrentCopyPath(Guid id) => Path.Combine(CacheFolder, $"{id:N}.torrent");

    /// <summary>Settings for torrents started from now on (a running engine keeps its port).</summary>
    public void ApplyOptions(EngineOptions options) => _options = options;

    public TorrentSettings TorrentSettings() => new TorrentSettingsBuilder
    {
        AllowDht = _options.TorrentDht,
        AllowPeerExchange = true,
        AllowInitialSeeding = false,
        CreateContainingDirectory = true,
        MaximumConnections = Math.Max(1, _options.TorrentMaxConnectionsPerTorrent),
        UploadSlots = 8,
    }.ToSettings();

    public async Task<TorrentManager> AddAsync(Torrent torrent, string folder)
    {
        var engine = await EngineAsync().ConfigureAwait(false);
        if (Find(engine, torrent.InfoHashes) is { } stale)
            await RemoveAsync(stale).ConfigureAwait(false);
        return await engine.AddAsync(torrent, folder, TorrentSettings()).ConfigureAwait(false);
    }

    public async Task<TorrentManager> AddAsync(MagnetLink magnet, string folder)
    {
        var engine = await EngineAsync().ConfigureAwait(false);
        if (Find(engine, magnet.InfoHashes) is { } stale)
            await RemoveAsync(stale).ConfigureAwait(false);
        return await engine.AddAsync(magnet, folder, TorrentSettings()).ConfigureAwait(false);
    }

    /// <summary>Stops the torrent (fast resume is saved on stop) and unloads it, keeping the downloaded data.</summary>
    public async Task RemoveAsync(TorrentManager manager, bool forgetCache = false)
    {
        try
        {
            if (manager.State is not (TorrentState.Stopped or TorrentState.Error))
                await manager.StopAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"torrent: stop {manager.Name} failed: {ex.Message}");
        }

        try
        {
            if (_engine is { Disposed: false } engine && engine.Contains(manager))
                await engine.RemoveAsync(manager, forgetCache ? RemoveMode.CacheDataOnly : RemoveMode.KeepAllData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"torrent: unload {manager.Name} failed: {ex.Message}");
        }
    }

    /// <summary>Deletes the engine's fast-resume data and cached metadata of a torrent removed from the list.</summary>
    public void Forget(string? infoHashHex)
    {
        if (string.IsNullOrEmpty(infoHashHex))
            return;
        foreach (var dir in new[] { "fastresume", "metadata" })
        {
            var folder = Path.Combine(CacheFolder, dir);
            if (!Directory.Exists(folder))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, infoHashHex + ".*"))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"torrent: cannot delete cache of {infoHashHex}: {ex.Message}");
            }
        }
    }

    private static TorrentManager? Find(ClientEngine engine, InfoHashes hashes)
    {
        foreach (var m in engine.Torrents)
        {
            if (m.InfoHashes.Equals(hashes) || (hashes.V1 is { } v1 && m.InfoHashes.Contains(v1))
                || (hashes.V2 is { } v2 && m.InfoHashes.Contains(v2)))
                return m;
        }
        return null;
    }

    private async Task<ClientEngine> EngineAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engine is not null)
                return _engine;

            Directory.CreateDirectory(CacheFolder);
            var options = _options;
            var address = options.TorrentListenAddress ?? IPAddress.Any;
            var port = PickPort(address, options.TorrentPort);
            var listen = new Dictionary<string, IPEndPoint> { ["ipv4"] = new IPEndPoint(address, port) };
            if (options.TorrentListenAddress is null && Socket.OSSupportsIPv6)
                listen["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, port);

            var settings = new EngineSettingsBuilder
            {
                // Encryption allowed and preferred; plain text still accepted (more peers).
                AllowedEncryption = [EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText],
                AllowLocalPeerDiscovery = options.TorrentLocalPeerDiscovery,
                AllowPortForwarding = options.TorrentPortForwarding,
                AutoSaveLoadDhtCache = options.TorrentDht,
                AutoSaveLoadFastResume = true,
                AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = CacheFolder,
                DhtEndPoint = options.TorrentDht ? new IPEndPoint(address, port) : null,
                ListenEndPoints = listen,
                FastResumeMode = FastResumeMode.BestEffort,
                MaximumConnections = Math.Max(1, options.TorrentMaxConnectionsPerTorrent) * 3,
                MaximumHalfOpenConnections = 32,
                MaximumOpenFiles = 64,
                DiskCacheBytes = 32 << 20,
                MaximumUploadRate = Math.Max(0, options.TorrentMaxUploadRate),
                MaximumDownloadRate = Math.Max(0, options.TorrentMaxDownloadRate),
                UsePartialFiles = true,   // "name.!mt" until the file is complete
                WebSeedDelay = TimeSpan.FromSeconds(5),   // torrents with HTTP seeds (archive.org…) start fast
            }.ToSettings();

            var engine = new ClientEngine(settings);
            engine.CriticalException += (_, e) => _log.Error("torrent engine failure", e.Exception);
            _engine = engine;
            ListenPort = port;
            _log.Info($"torrent engine: port {port}, DHT {OnOff(options.TorrentDht)}, LPD {OnOff(options.TorrentLocalPeerDiscovery)}, " +
                      $"port forwarding {OnOff(options.TorrentPortForwarding)}");
            return engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>The configured port when both TCP and UDP are free on it, otherwise a random free one.</summary>
    private int PickPort(IPAddress address, int preferred)
    {
        if (preferred is > 0 and <= 65535 && IsFree(address, preferred))
            return preferred;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            int port;
            var probe = new TcpListener(address, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            if (IsFree(address, port))
            {
                if (preferred > 0)
                    _log.Warn($"torrent engine: port {preferred} is busy, using {port}");
                return port;
            }
        }
        return preferred;
    }

    private static bool IsFree(IPAddress address, int port)
    {
        try
        {
            var tcp = new TcpListener(address, port);
            tcp.Start();
            tcp.Stop();
            using var udp = new UdpClient(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ClientEngine? engine;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            engine = _engine;
        }
        finally
        {
            _gate.Release();
        }

        if (engine is null)
            return;
        try
        {
            foreach (var m in engine.Torrents.ToList())
                await RemoveAsync(m).ConfigureAwait(false);
            await engine.StopAllAsync(StopTimeout).ConfigureAwait(false);   // DHT (saves its node cache), port mappings
        }
        catch (Exception ex)
        {
            _log.Warn($"torrent engine: stop failed: {ex.Message}");
        }
        finally
        {
            engine.Dispose();
            _log.Info("torrent engine: stopped");
        }
    }
}
