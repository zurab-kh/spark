using System.Diagnostics;
using System.Net;
using MonoTorrent;
using Strela.Engine.Http;
using Strela.Engine.Manager;
using Strela.Engine.Torrents;

namespace Strela.Engine.Tests;

/// <summary>Spark's torrent engine against a local seeder on 127.0.0.1 (no DHT, trackers, LPD or UPnP).</summary>
public sealed class TorrentTests : IAsyncLifetime
{
    private const int MB = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "strela-tests", "tor-" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly HttpClient _http = StrelaHttp.Create();
    private readonly List<DownloadManager> _managers = [];
    private readonly List<TorrentSeeder> _seeders = [];
    private readonly List<ThrottledProxy> _proxies = [];

    private string DownloadFolder => Path.Combine(_root, "dl");
    private string StateFolder => Path.Combine(_root, "state");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
            await manager.DisposeAsync();
        foreach (var proxy in _proxies)
            await proxy.DisposeAsync();
        foreach (var seeder in _seeders)
            await seeder.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task SingleFile_Torrent_DownloadsVerifiedFile_AndStops()
    {
        var content = TestData.Random(6 * MB + 12345, 1);
        var seeder = await Seed("single", new() { ["movie.bin"] = content });
        var manager = await NewManager(Options(seeder));

        var id = manager.Add(new DownloadRequest(new Uri(await TorrentFile(seeder))));
        var done = await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed);

        var snap = done.Single();
        Assert.Equal(id, snap.Id);
        Assert.Equal(DownloadKind.Torrent, snap.Kind);
        Assert.Equal(Path.Combine(DownloadFolder, "movie.bin"), snap.FilePath);
        Assert.Equal(content.Length, snap.TotalBytes);
        Assert.Equal(content.Length, snap.DownloadedBytes);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(snap.FilePath!));
        Assert.False(File.Exists(snap.FilePath + ".!mt"));
        Assert.False(manager.HasActiveWork);

        // Not seeding: the torrent is out of the engine and the seeder loses its peer.
        Assert.Equal(0, manager.Torrents.LoadedTorrents);
        await Until(() => seeder.Manager.OpenConnections == 0, "seeder still connected");
    }

    [Fact]
    public async Task MultiFile_Torrent_DownloadsIntoFolderNamedAfterTorrent()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["a.bin"] = TestData.Random(3 * MB, 2),
            ["sub/b.bin"] = TestData.Random(2 * MB + 7, 3),
            ["sub/deeper/c.txt"] = TestData.Random(1000, 4),
        };
        var seeder = await Seed("Pack", files);
        var manager = await NewManager(Options(seeder));

        manager.Add(new DownloadRequest(new Uri(await TorrentFile(seeder))));
        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed)).Single();

        var folder = Path.Combine(DownloadFolder, "Pack");
        Assert.Equal(folder, snap.FilePath);
        Assert.Equal("Pack", snap.FileName);
        foreach (var (path, content) in files)
            Assert.Equal(TestData.Sha256(content), TestData.Sha256(Path.Combine(folder, path)));
        Assert.Empty(Directory.EnumerateFiles(folder, "*.!mt", SearchOption.AllDirectories));
        Assert.Equal(0, manager.Torrents.LoadedTorrents);
    }

    [Fact]
    public async Task Magnet_WithoutMetadata_GetsMetadataFromPeer()
    {
        var content = TestData.Random(4 * MB, 5);
        var seeder = await Seed("magnet", new() { ["linux.iso"] = content });
        var manager = await NewManager(Options(seeder));

        var magnet = seeder.MagnetLink("linux.iso");
        var id = manager.Add(new DownloadRequest(new Uri(magnet)));
        Assert.Equal(TorrentJob.Hex(seeder.Torrent.InfoHashes), manager.StateOf(id)!.InfoHash);

        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed)).Single();
        Assert.Equal(magnet, snap.Url);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(snap.FilePath!));
        var state = manager.StateOf(id)!;
        Assert.NotNull(state.TorrentFilePath);
        Assert.True(File.Exists(state.TorrentFilePath));   // metadata kept: a restart needs no peers for it
        Assert.Equal(content.Length, state.TotalBytes);
    }

    [Fact]
    public async Task Magnet_NoPeers_FailsWithMetadataError()
    {
        var manager = await NewManager(Options((IPEndPoint?)null) with { TorrentMetadataTimeout = TimeSpan.FromSeconds(1) });
        var hash = Convert.ToHexString(TestData.Random(20, 6));
        manager.Add(new DownloadRequest(new Uri($"magnet:?xt=urn:btih:{hash}&dn=nothing")));

        await WaitFor(manager, s => s.Single().Status is DownloadStatus.Error
            || s.Single().StatusText == TorrentJob.SearchingText);
        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Error)).Single();
        Assert.Equal("Не удалось получить метаданные торрента", snap.StatusText);
        Assert.Equal(0, manager.Torrents.LoadedTorrents);
    }

    [Fact]
    public async Task Pause_KeepsData_ResumeFinishesWithoutDownloadingAgain()
    {
        var content = TestData.Random(10 * MB, 7);
        var seeder = await Seed("pause", new() { ["big.bin"] = content });
        var manager = await NewManager(Options(Throttle(seeder, 2 * MB)));

        var id = manager.Add(new DownloadRequest(new Uri(await TorrentFile(seeder))));
        await WaitFor(manager, s => s.Single().DownloadedBytes > 2 * MB);
        manager.Pause(id);
        var paused = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Paused && manager.Torrents.LoadedTorrents == 0)).Single();
        Assert.True(paused.DownloadedBytes > MB, $"paused at {paused.DownloadedBytes}");

        await Task.Delay(500);
        var still = manager.GetSnapshots().Single();
        Assert.Equal(DownloadStatus.Paused, still.Status);
        Assert.Equal(paused.DownloadedBytes, still.DownloadedBytes);

        manager.Resume(id);
        var live = await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed
            || s.Single().StatusText?.StartsWith("сиды", StringComparison.Ordinal) == true);
        if (live.Single().Status != DownloadStatus.Completed)
            Assert.Matches(@"^сиды \d+ · пиры \d+$", live.Single().StatusText);
        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed)).Single();
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(snap.FilePath!));
        Assert.True(seeder.DataBytesSent < content.Length * 1.3, $"seeder sent {seeder.DataBytesSent} for {content.Length}");
    }

    [Fact]
    public async Task ManagerRestart_ResumesFromFastResume()
    {
        var content = TestData.Random(10 * MB, 8);
        var seeder = await Seed("restart", new() { ["data.bin"] = content });
        var options = Options(Throttle(seeder, 2 * MB));
        var first = await NewManager(options);

        var id = first.Add(new DownloadRequest(new Uri(await TorrentFile(seeder))));
        await WaitFor(first, s => s.Single().DownloadedBytes > 3 * MB);
        await first.DisposeAsync();   // app closed mid-download
        var saved = first.GetSnapshots().Single();
        _managers.Remove(first);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(StateFolder, "torrents", "fastresume"), "*.fresume"));

        var second = new DownloadManager(options, _log, _http);
        _managers.Add(second);
        await second.InitializeAsync();
        var resumed = second.GetSnapshots().Single();
        Assert.Equal(id, resumed.Id);
        Assert.True(resumed.DownloadedBytes > 2 * MB, $"restart shows {resumed.DownloadedBytes}, had {saved.DownloadedBytes}");

        var snap = (await WaitFor(second, s => s.Single().Status == DownloadStatus.Completed)).Single();
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(snap.FilePath!));
        Assert.True(seeder.DataBytesSent < content.Length * 1.3, $"seeder sent {seeder.DataBytesSent} for {content.Length}");
        Assert.False(_log.Contains("Error", "torrent"), string.Join("\n", _log.Entries.Select(e => e.Message)));
    }

    [Fact]
    public async Task SameTorrent_AddedTwice_IsOneDownload()
    {
        var seeder = await Seed("dup", new() { ["dup.bin"] = TestData.Random(MB, 9) });
        var manager = await NewManager(Options(seeder));
        var file = await TorrentFile(seeder);

        var a = manager.Add(new DownloadRequest(new Uri(file)));
        var b = manager.Add(new DownloadRequest(new Uri(seeder.MagnetLink())));
        var c = manager.Add(new DownloadRequest(new Uri(file)));
        Assert.Equal(a, b);
        Assert.Equal(a, c);

        await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed);
        Assert.Equal(a, manager.Add(new DownloadRequest(new Uri(seeder.MagnetLink()))));   // done, data on disk
        Assert.Single(manager.GetSnapshots());
    }

    [Fact]
    public async Task RemoveWithFiles_DeletesOnlyTheTorrentsData()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["one.bin"] = TestData.Random(MB, 10),
            ["dir/two.bin"] = TestData.Random(MB, 11),
        };
        var seeder = await Seed("Album", files);
        var manager = await NewManager(Options(seeder));
        var mine = Path.Combine(DownloadFolder, "Album", "my-notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        await File.WriteAllTextAsync(mine, "keep me");

        var id = manager.Add(new DownloadRequest(new Uri(await TorrentFile(seeder))));
        await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed);
        var copy = manager.StateOf(id)!.TorrentFilePath!;
        await manager.RemoveAsync(id, deleteFile: true);

        Assert.Empty(manager.GetSnapshots());
        Assert.False(File.Exists(Path.Combine(DownloadFolder, "Album", "one.bin")));
        Assert.False(Directory.Exists(Path.Combine(DownloadFolder, "Album", "dir")));
        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task TorrentLink_OverHttp_IsFetchedAndDownloaded()
    {
        var content = TestData.Random(2 * MB, 12);
        var seeder = await Seed("http", new() { ["via-http.bin"] = content });
        await using var server = await Strela.TestServer.TestFileServer.StartAsync();
        var url = server.AddFile("file.torrent", seeder.TorrentBytes);
        var manager = await NewManager(Options(seeder));

        var id = manager.Add(new DownloadRequest(url));
        Assert.Equal(DownloadKind.Torrent, manager.StateOf(id)!.Kind);
        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed)).Single();
        Assert.Equal("via-http.bin", snap.FileName);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(snap.FilePath!));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private async Task<TorrentSeeder> Seed(string name, Dictionary<string, byte[]> files)
    {
        var seeder = await TorrentSeeder.StartAsync(Path.Combine(_root, name), name, files);
        _seeders.Add(seeder);
        return seeder;
    }

    /// <summary>The seeder behind a proxy that sends at most <paramref name="bytesPerSecond"/> to Spark.</summary>
    private IPEndPoint Throttle(TorrentSeeder seeder, long bytesPerSecond)
    {
        var proxy = new ThrottledProxy(seeder.EndPoint, bytesPerSecond);
        _proxies.Add(proxy);
        return proxy.EndPoint;
    }

    private async Task<string> TorrentFile(TorrentSeeder seeder)
    {
        var path = Path.Combine(_root, "files", Guid.NewGuid().ToString("N") + ".torrent");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, seeder.TorrentBytes);
        return path;
    }

    private EngineOptions Options(TorrentSeeder? seeder) => Options(seeder?.EndPoint);

    private EngineOptions Options(IPEndPoint? peer) => new()
    {
        DownloadFolder = DownloadFolder,
        StateFolder = StateFolder,
        TorrentPort = TorrentSeeder.FreePort(),
        TorrentDht = false,
        TorrentLocalPeerDiscovery = false,
        TorrentPortForwarding = false,
        TorrentListenAddress = IPAddress.Loopback,
        TorrentExtraPeers = peer is null ? [] : [peer],
        StateSaveInterval = TimeSpan.FromMilliseconds(200),
    };

    private async Task<DownloadManager> NewManager(EngineOptions options)
    {
        var manager = new DownloadManager(options, _log, _http);
        _managers.Add(manager);
        await manager.InitializeAsync();
        return manager;
    }

    private static async Task<IReadOnlyList<DownloadSnapshot>> WaitFor(
        DownloadManager manager, Func<IReadOnlyList<DownloadSnapshot>, bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var snaps = manager.GetSnapshots();
            if (snaps.Count > 0 && done(snaps))
                return snaps;
            if (clock.Elapsed > Timeout)
                throw new TimeoutException("condition not reached: " +
                    string.Join("; ", snaps.Select(s => $"{s.FileName} {s.Status} {s.DownloadedBytes}/{s.TotalBytes} {s.StatusText}")));
            await Task.Delay(50);
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }
}
