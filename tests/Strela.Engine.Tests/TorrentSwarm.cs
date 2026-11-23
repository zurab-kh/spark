using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;

namespace Strela.Engine.Tests;

/// <summary>
/// A seeding MonoTorrent engine on 127.0.0.1 with one torrent: no DHT, no trackers, no local peer discovery,
/// no port mapping. Spark's engine gets the seeder as a manually added peer.
/// </summary>
public sealed class TorrentSeeder : IAsyncDisposable
{
    private readonly ClientEngine _engine;

    private TorrentSeeder(ClientEngine engine, TorrentManager manager, Torrent torrent, byte[] torrentBytes, int port)
    {
        _engine = engine;
        Manager = manager;
        Torrent = torrent;
        TorrentBytes = torrentBytes;
        Port = port;
    }

    public TorrentManager Manager { get; }
    public Torrent Torrent { get; }
    public byte[] TorrentBytes { get; }
    public int Port { get; }
    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    /// <summary>Bytes of file data the seeder has sent to peers so far.</summary>
    public long DataBytesSent => Manager.Monitor.DataBytesSent;

    public string MagnetLink(string? name = null) =>
        new MagnetLink(Torrent.InfoHashes, name, [], [], null).ToV1String();

    /// <summary>
    /// Seeds <paramref name="files"/> (relative path → content) from <c>root/seed</c>: one file without a folder
    /// gives a single-file torrent, otherwise a folder named <paramref name="name"/>.
    /// </summary>
    public static async Task<TorrentSeeder> StartAsync(string root, string name, IReadOnlyDictionary<string, byte[]> files,
        int uploadBytesPerSecond = 0, TorrentType type = TorrentType.V1Only)
    {
        var seedFolder = Path.Combine(root, "seed");
        string source;
        if (files.Count == 1 && !files.Keys.Single().Contains('/'))
        {
            source = Path.Combine(seedFolder, files.Keys.Single());
            Directory.CreateDirectory(seedFolder);
            await File.WriteAllBytesAsync(source, files.Values.Single());
        }
        else
        {
            source = Path.Combine(seedFolder, name);
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(source, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, content);
            }
        }

        var creator = new TorrentCreator(type);
        BEncodedDictionary dict = await creator.CreateAsync(new TorrentFileSource(source));
        var bytes = dict.Encode();
        var torrent = Torrent.Load(bytes);

        var port = FreePort();
        var settings = new EngineSettingsBuilder
        {
            AllowLocalPeerDiscovery = false,
            AllowPortForwarding = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            CacheDirectory = Path.Combine(root, "seed-cache"),
            DhtEndPoint = null,
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, port) },
            MaximumUploadRate = uploadBytesPerSecond,
        }.ToSettings();
        var engine = new ClientEngine(settings);
        var manager = await engine.AddAsync(torrent, seedFolder, new TorrentSettingsBuilder { AllowDht = false }.ToSettings());
        await manager.StartAsync();

        var clock = Stopwatch.StartNew();
        while (manager.State != TorrentState.Seeding)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"seeder stuck in {manager.State}");
            await Task.Delay(20);
        }
        return new TorrentSeeder(engine, manager, torrent, bytes, port);
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Manager.StopAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // test teardown
        }
        _engine.Dispose();
    }
}

/// <summary>
/// A TCP proxy in front of the seeder that limits seeder → client traffic (MonoTorrent does not rate-limit
/// peers on the local machine, so the seeder's own limit has no effect on 127.0.0.1).
/// </summary>
public sealed class ThrottledProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly long _bytesPerSecond;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _accept;

    public ThrottledProxy(IPEndPoint target, long bytesPerSecond)
    {
        _target = target;
        _bytesPerSecond = bytesPerSecond;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => RelayAsync(client));
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using var upstream = new TcpClient();
        lock (_clients)
        {
            _clients.Add(client);
            _clients.Add(upstream);
        }
        try
        {
            await upstream.ConnectAsync(_target, _stop.Token);
            using var token = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var a = PumpAsync(client.GetStream(), upstream.GetStream(), 0, token.Token);
            var b = PumpAsync(upstream.GetStream(), client.GetStream(), _bytesPerSecond, token.Token);
            await Task.WhenAny(a, b);
            await token.CancelAsync();
        }
        catch (Exception)
        {
            // a peer went away
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, long bytesPerSecond, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var clock = Stopwatch.StartNew();
        long sent = 0;
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, ct);
                if (read == 0)
                    return;
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                sent += read;
                if (bytesPerSecond > 0)
                {
                    var due = TimeSpan.FromSeconds((double)sent / bytesPerSecond) - clock.Elapsed;
                    if (due > TimeSpan.Zero)
                        await Task.Delay(due, ct);
                }
            }
        }
        catch (Exception)
        {
            // closed
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var c in _clients)
                c.Dispose();
        }
        try { await _accept; } catch (Exception) { }
        _stop.Dispose();
    }
}
