using System.Diagnostics;
using Strela.Engine.Http;
using Strela.Engine.Manager;
using Strela.Engine.Storage;
using Strela.TestServer;

namespace Strela.Engine.Tests;

public sealed class DownloadManagerTests : IAsyncLifetime
{
    private const int MB = 1024 * 1024;
    private const long OneMBps = 1_000_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "strela-tests", Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly HttpClient _http = StrelaHttp.Create();
    private readonly List<DownloadManager> _managers = [];
    private TestFileServer _server = null!;

    private string DownloadFolder => Path.Combine(_root, "dl");
    private string StateFolder => Path.Combine(_root, "state");

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
            await manager.DisposeAsync();
        _http.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task MaxActive_Respected()
    {
        var files = Enumerable.Range(0, 5).Select(i => TestData.Random(8 * MB, 100 + i)).ToArray();
        var manager = await NewManager(Options() with { MaxActiveDownloads = 3, ConnectionsPerFile = 4 });
        var completed = 0;
        manager.Completed += _ => Interlocked.Increment(ref completed);

        var ids = files.Select((content, i) => manager.Add(new DownloadRequest(
            _server.AddFile($"f{i}.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps))))).ToArray();

        var maxSeen = 0;
        await WaitFor(manager, snaps =>
        {
            var downloading = snaps.Count(s => s.Status == DownloadStatus.Downloading);
            Assert.True(downloading <= 3, $"{downloading} downloading at once");
            maxSeen = Math.Max(maxSeen, downloading);
            return snaps.All(s => s.Status == DownloadStatus.Completed);
        });

        Assert.Equal(3, maxSeen);
        Assert.Equal(5, Volatile.Read(ref completed));
        var byId = manager.GetSnapshots().ToDictionary(s => s.Id);
        for (var i = 0; i < files.Length; i++)
            Assert.Equal(TestData.Sha256(files[i]), TestData.Sha256(byId[ids[i]].FilePath!));
        Assert.False(manager.HasActiveWork);
    }

    [Fact]
    public async Task Duplicate_ReturnsExistingId()
    {
        var url = _server.AddFile("a.bin", TestData.Random(16 * MB, 1), new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var manager = await NewManager(Options() with { ConnectionsPerFile = 2 });

        var id = manager.Add(new DownloadRequest(url));
        await WaitFor(manager, snaps => snaps.Single().Status == DownloadStatus.Downloading);
        var again = manager.Add(new DownloadRequest(url));

        Assert.Equal(id, again);
        Assert.Single(manager.GetSnapshots());
    }

    // The browser can fire the same download twice (a site's download dialog left open, a double click):
    // a finished download whose file is still on disk must not be fetched again.
    [Fact]
    public async Task Duplicate_OfCompletedDownload_IsNotDownloadedAgain()
    {
        var url = _server.AddFile("done.bin", TestData.Random(2 * MB, 7));
        var manager = await NewManager(Options());

        var id = manager.Add(new DownloadRequest(url));
        await WaitFor(manager, snaps => snaps.Single().Status == DownloadStatus.Completed);
        var again = manager.Add(new DownloadRequest(url));

        Assert.Equal(id, again);
        Assert.Single(manager.GetSnapshots());
        Assert.Single(Directory.GetFiles(DownloadFolder, "done*"));
    }

    [Fact]
    public async Task Duplicate_OfCompletedDownload_WhoseFileWasDeleted_DownloadsAgain()
    {
        var url = _server.AddFile("gone.bin", TestData.Random(2 * MB, 8));
        var manager = await NewManager(Options());

        var id = manager.Add(new DownloadRequest(url));
        await WaitFor(manager, snaps => snaps.Single().Status == DownloadStatus.Completed);
        File.Delete(manager.GetSnapshots().Single().FilePath!);
        var again = manager.Add(new DownloadRequest(url));

        Assert.NotEqual(id, again);
        await WaitFor(manager, snaps => snaps.Count == 2 && snaps.All(s => s.Status == DownloadStatus.Completed));
    }

    [Fact]
    public async Task PauseResume_CompletesCorrectly()
    {
        var content = TestData.Random(16 * MB, 2);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var manager = await NewManager(Options() with { ConnectionsPerFile = 4 });

        var id = manager.Add(new DownloadRequest(url));
        await WaitFor(manager, snaps => snaps.Single().DownloadedBytes > MB);
        manager.Pause(id);
        Assert.Equal(DownloadStatus.Paused, manager.GetSnapshots().Single().Status);
        await Task.Delay(TimeSpan.FromSeconds(1));
        var paused = manager.GetSnapshots().Single();
        Assert.Equal(DownloadStatus.Paused, paused.Status);
        Assert.Equal(0, paused.Connections);
        Assert.False(manager.HasActiveWork);

        manager.Resume(id);
        var done = await WaitFor(manager, snaps => snaps.Single().Status == DownloadStatus.Completed);

        Assert.Equal(TestData.Sha256(content), TestData.Sha256(done.Single().FilePath!));
        Assert.Equal(content.Length, done.Single().DownloadedBytes);
    }

    [Fact]
    public async Task Restart_ResumesAutomatically()
    {
        var content = TestData.Random(16 * MB, 3);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4 };
        var first = await NewManager(options);

        var id = first.Add(new DownloadRequest(url));
        await WaitFor(first, snaps => snaps.Single().DownloadedBytes > content.Length * 6L / 10);
        await first.DisposeAsync();

        var saved = Assert.Single(new StateStore(StateFolder).LoadAll(_log));
        Assert.Equal(DownloadStatus.Downloading, saved.Status);
        var savedBytes = saved.Segments.Sum(s => s.Position - s.Start);
        Assert.True(content.Length - savedBytes < content.Length / 2, $"saved only {savedBytes} bytes");

        _server.SetOptions(url, new ServeOptions());
        var second = await NewManager(options);
        var done = await WaitFor(second, snaps => snaps.Single().Status == DownloadStatus.Completed);

        Assert.Equal(id, done.Single().Id);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(done.Single().FilePath!));
        Assert.DoesNotContain(_log.Entries, e => e.Level == "Warn" &&
            (e.Message.Contains("starting over") || e.Message.Contains("again") || e.Message.Contains("restart")));
    }

    [Fact]
    public async Task PausedStaysPausedAfterRestart()
    {
        var url = _server.AddFile("a.bin", TestData.Random(16 * MB, 4), new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4 };
        var first = await NewManager(options);

        var id = first.Add(new DownloadRequest(url));
        await WaitFor(first, snaps => snaps.Single().DownloadedBytes > MB);
        first.Pause(id);
        await first.DisposeAsync();

        var second = await NewManager(options);
        var snap = second.GetSnapshots().Single();
        Assert.Equal(id, snap.Id);
        Assert.Equal(DownloadStatus.Paused, snap.Status);
        Assert.False(second.HasActiveWork);

        await Task.Delay(TimeSpan.FromSeconds(1));
        var later = second.GetSnapshots().Single();
        Assert.Equal(DownloadStatus.Paused, later.Status);
        Assert.Equal(snap.DownloadedBytes, later.DownloadedBytes);
        Assert.True(later.DownloadedBytes > 0);
    }

    [Fact]
    public async Task MissingPartFile_RestartsFromZero()
    {
        var content = TestData.Random(16 * MB, 5);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4 };
        var first = await NewManager(options);

        first.Add(new DownloadRequest(url));
        await WaitFor(first, snaps => snaps.Single().DownloadedBytes > MB);
        await first.DisposeAsync();

        var saved = Assert.Single(new StateStore(StateFolder).LoadAll(_log));
        File.Delete(saved.PartPath!);
        _server.SetOptions(url, new ServeOptions());
        var second = await NewManager(options);
        var done = await WaitFor(second, snaps => snaps.Single().Status == DownloadStatus.Completed);

        Assert.Equal(TestData.Sha256(content), TestData.Sha256(done.Single().FilePath!));
        Assert.True(_log.Contains("Warn", "part file is missing"), "no warning about the missing part file");
    }

    [Fact]
    public async Task SameName_TwoFiles()
    {
        var a = TestData.Random(4 * MB, 6);
        var b = TestData.Random(4 * MB, 7);
        var slow = new ServeOptions(BytesPerSecondPerConnection: OneMBps);
        var manager = await NewManager(Options() with { ConnectionsPerFile = 4 });

        var idA = manager.Add(new DownloadRequest(_server.AddFile("same.bin", a, slow)));
        var idB = manager.Add(new DownloadRequest(_server.AddFile("same.bin", b, slow)));
        var done = await WaitFor(manager, snaps => snaps.All(s => s.Status == DownloadStatus.Completed));

        var byId = done.ToDictionary(s => s.Id);
        Assert.Equal(
            new[] { "same (1).bin", "same.bin" },
            done.Select(s => Path.GetFileName(s.FilePath!)).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(TestData.Sha256(a), TestData.Sha256(byId[idA].FilePath!));
        Assert.Equal(TestData.Sha256(b), TestData.Sha256(byId[idB].FilePath!));
    }

    [Fact]
    public async Task BadFolder_ErrorNotCrash()
    {
        Directory.CreateDirectory(_root);
        var notAFolder = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(notAFolder, "x");
        var url = _server.AddFile("a.bin", TestData.Random(MB, 8));
        var manager = await NewManager(Options() with { DownloadFolder = notAFolder });

        manager.Add(new DownloadRequest(url));
        var snaps = await WaitFor(manager, s => s.Single().Status == DownloadStatus.Error);

        Assert.StartsWith("Нет доступа к папке", snaps.Single().StatusText);
        Assert.False(manager.HasActiveWork);
    }

    [Fact]
    public async Task UpdateUrl_KeepsProgressWhenSizeMatches()
    {
        var content = TestData.Random(16 * MB, 9);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var manager = await NewManager(Options() with { ConnectionsPerFile = 4 });

        var id = manager.Add(new DownloadRequest(url));
        await WaitFor(manager, snaps => snaps.Single().DownloadedBytes > 2 * MB);
        manager.Pause(id);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        _server.SetOptions(url, new ServeOptions(FixedStatus: 403));
        manager.Resume(id);
        var failed = await WaitFor(manager, snaps => snaps.Single().Status == DownloadStatus.Error);
        Assert.Equal("Ссылка устарела — обновите ссылку", failed.Single().StatusText);
        var progress = failed.Single().DownloadedBytes;
        Assert.True(progress > 2 * MB);

        var mirror = _server.AddFile("a.bin", content);
        Assert.True(await manager.UpdateUrlAsync(id, mirror));
        var queued = manager.GetSnapshots().Single();
        Assert.True(queued.Status is DownloadStatus.Queued or DownloadStatus.Downloading);
        Assert.True(queued.DownloadedBytes >= progress, $"progress {queued.DownloadedBytes} < {progress}");
        var done = await WaitFor(manager, snaps => snaps.Single().Status == DownloadStatus.Completed);

        Assert.Equal(TestData.Sha256(content), TestData.Sha256(done.Single().FilePath!));
        Assert.Equal(mirror.AbsoluteUri, done.Single().Url);
        Assert.DoesNotContain(_log.Entries, e => e.Level == "Warn" &&
            (e.Message.Contains("starting over") || e.Message.Contains("again") || e.Message.Contains("restart")));
    }

    [Fact]
    public async Task Remove_WithFile_DeletesFiles()
    {
        var small = _server.AddFile("done.bin", TestData.Random(MB, 10));
        var big = _server.AddFile("part.bin", TestData.Random(16 * MB, 11), new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var manager = await NewManager(Options() with { ConnectionsPerFile = 4 });

        var doneId = manager.Add(new DownloadRequest(small));
        var partId = manager.Add(new DownloadRequest(big));
        await WaitFor(manager, snaps =>
            snaps.Single(s => s.Id == doneId).Status == DownloadStatus.Completed &&
            snaps.Single(s => s.Id == partId).DownloadedBytes > MB);
        var finalPath = manager.GetSnapshots().Single(s => s.Id == doneId).FilePath!;
        var partPath = manager.GetSnapshots().Single(s => s.Id == partId).FilePath!;
        Assert.True(File.Exists(finalPath));
        Assert.True(File.Exists(partPath));

        await manager.RemoveAsync(doneId, deleteFile: true);
        await manager.RemoveAsync(partId, deleteFile: true);   // removed while downloading

        Assert.Empty(manager.GetSnapshots());
        Assert.False(File.Exists(finalPath));
        Assert.False(File.Exists(partPath));
        Assert.Empty(Directory.GetFiles(StateFolder));
        Assert.Empty(Directory.GetFiles(DownloadFolder));
        Assert.False(manager.HasActiveWork);
    }

    [Fact]
    public async Task GetSnapshots_IsFast()
    {
        var store = new StateStore(StateFolder);
        Parallel.For(0, 1000, i => store.Save(new DownloadState
        {
            Id = Guid.NewGuid(),
            OriginalUrl = $"http://127.0.0.1:1/f/{i}/file{i}.bin",
            CurrentUrl = $"http://127.0.0.1:1/f/{i}/file{i}.bin",
            Folder = DownloadFolder,
            FileName = $"file{i}.bin",
            TotalBytes = 10 * MB,
            Segments = [new SegmentState(0, 10 * MB, i)],
            Status = DownloadStatus.Paused,
            QueueOrder = i,
            AddedAt = DateTimeOffset.UtcNow,
        }));
        var manager = await NewManager(Options());
        Assert.Equal(1000, manager.GetSnapshots().Count);
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (long)i), manager.GetSnapshots().Select(s => s.QueueOrder));

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            manager.GetSnapshots();
        clock.Stop();

        var average = clock.Elapsed.TotalMilliseconds / 100;
        Assert.True(average < 5, $"GetSnapshots took {average:0.###} ms on average");
    }

    [Fact]
    public async Task Initialize_SkipsBrokenStates()
    {
        Directory.CreateDirectory(StateFolder);
        await File.WriteAllTextAsync(Path.Combine(StateFolder, $"{Guid.NewGuid():D}.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(StateFolder, $"{Guid.NewGuid():D}.json"),
            $"{{\"Id\":\"{Guid.NewGuid():D}\",\"CurrentUrl\":\"ftp://x/y\",\"Status\":\"Queued\"}}");
        await File.WriteAllTextAsync(Path.Combine(StateFolder, $"{Guid.NewGuid():D}.json"),
            $"{{\"Id\":\"{Guid.NewGuid():D}\",\"CurrentUrl\":\"http://127.0.0.1:1/a\",\"Segments\":null}}");

        var manager = await NewManager(Options());

        Assert.Empty(manager.GetSnapshots());
        Assert.Equal(3, _log.Entries.Count(e => e.Level == "Warn" && e.Message.StartsWith("state: skip", StringComparison.Ordinal)));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    // Fixed 16 connections: these tests depend on the connection count (auto mode has its own tests).
    private EngineOptions Options() => new() { DownloadFolder = DownloadFolder, StateFolder = StateFolder, ConnectionsPerFile = 16 };

    private async Task<DownloadManager> NewManager(EngineOptions options)
    {
        var manager = new DownloadManager(options, _log, _http);
        _managers.Add(manager);
        await manager.InitializeAsync();
        return manager;
    }

    /// <summary>Polls the snapshots every 50 ms until <paramref name="done"/> holds; fails after <see cref="Timeout"/>.</summary>
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
                    string.Join("; ", snaps.Select(s => $"{s.FileName} {s.Status} {s.DownloadedBytes} {s.StatusText}")));
            await Task.Delay(50);
        }
    }
}
