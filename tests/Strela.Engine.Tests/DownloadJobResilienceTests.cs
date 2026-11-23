using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strela.Engine.Http;
using Strela.Engine.Jobs;
using Strela.Engine.Manager;
using Strela.Engine.Policies;
using Strela.Engine.Storage;
using Strela.TestServer;

namespace Strela.Engine.Tests;

public sealed class DownloadJobResilienceTests : IAsyncLifetime
{
    private const int MB = 1024 * 1024;
    private const long OneMBps = 1_000_000;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "strela-tests", Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly HttpClient _http = StrelaHttp.Create();
    private TestFileServer _server = null!;

    private string DownloadFolder => Path.Combine(_root, "dl");
    private string StateFolder => Path.Combine(_root, "state");

    public async Task InitializeAsync() => _server = await TestFileServer.StartAsync();

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task DropsMidStream_StillCorrect()
    {
        // 4 MB segments: every response is cut after at most 1 MB, so each segment needs several requests
        // (with 16 connections the 1 MB segments would fit exactly and nothing would ever drop).
        var content = TestData.Random(16 * MB, 21);
        var url = _server.AddFile("a.bin", content, new ServeOptions(DropAfterBytes: MB));

        var (_, outcome) = await Run(NewState(url), Options() with { ConnectionsPerFile = 4 });

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        // Each response carries at most 1 MB: at least 16 data requests besides the probe.
        Assert.True(_server.ConnectionsOpened(url) >= 1 + 16, $"connections {_server.ConnectionsOpened(url)}: drops were not retried");
    }

    [Fact]
    public async Task Server503_LowersConnections()
    {
        var content = TestData.Random(16 * MB, 22);
        // 4 MB/s per connection: a response lasts ~250 ms, so the server cannot finish one (and admit a fifth
        // request) before the client has seen the 503 wave — unthrottled localhost answers 1 MB faster than
        // the client reads it, and the client would count 5 connections receiving.
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: 4 * OneMBps, MaxConcurrent: 4));
        var limits = new HostLimits();

        var (_, outcome) = await Run(NewState(url), Options(), limits);

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.True(limits.Ceiling(url.Host, 16) <= 4,
            $"ceiling {limits.Ceiling(url.Host, 16)}; {string.Join(" | ", _log.Entries.Where(e => e.Message.Contains("throttle")).Select(e => e.Message))}");
    }

    [Fact]
    public async Task Auto_MaxConcurrent6_StaysWithinServerLimit()
    {
        // Auto starts with 8 against a server that admits 6: two get 503 and leave, the host ceiling drops,
        // and auto never grows past it. After the first reject at most 6 + 1 (a reject still in flight) are open.
        var content = TestData.Random(32 * MB, 24);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: 2 * OneMBps, MaxConcurrent: 6));
        var options = Options() with { ConnectionsPerFile = 0, AutoTuneInterval = TimeSpan.FromSeconds(0.5) };
        Directory.CreateDirectory(options.DownloadFolder);
        var job = NewJob(NewState(url), options);

        using var sampling = new CancellationTokenSource();
        var maxAfterReject = 0;
        var sampler = Task.Run(async () =>
        {
            var rejected = false;
            while (!sampling.IsCancellationRequested)
            {
                rejected = rejected || LogHas("throttle");
                if (rejected)
                    maxAfterReject = Math.Max(maxAfterReject, job.ActiveConnections);
                await Task.Delay(2);
            }
        });
        var outcome = await job.RunAsync(CancellationToken.None);
        sampling.Cancel();
        await sampler;

        var autoLog = string.Join(" | ", _log.Entries.Where(e => e.Message.StartsWith("auto:", StringComparison.Ordinal)
                                                              || e.Message.StartsWith("throttle", StringComparison.Ordinal)).Select(e => e.Message));
        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.True(LogHas("throttle"), "the server never rejected a connection");
        Assert.True(_server.MaxConcurrentSeen(url) <= 6, $"server saw {_server.MaxConcurrentSeen(url)}");
        Assert.True(maxAfterReject <= 6 + 1, $"{maxAfterReject} open after the first reject; {autoLog}");
        Assert.Equal(8, job.PeakWorkers);   // never grew past the start
    }

    [Fact]
    public async Task SlowConnection_Replaced()
    {
        // Request 1 is the probe, so 2 slow requests make exactly the first data connection slow (50 KB/s).
        // Without replacement its segment would end as a ≥ 1 MB tail at 50 KB/s (> 20 s).
        var content = TestData.Random(16 * MB, 23);
        var url = _server.AddFile("a.bin", content,
            new ServeOptions(BytesPerSecondPerConnection: OneMBps, SlowConnectionCount: 2));

        var clock = Stopwatch.StartNew();
        var (_, outcome) = await Run(NewState(url), Options() with
        {
            ConnectionsPerFile = 4,
            SlowGrace = TimeSpan.FromSeconds(2),
            SlowCheckInterval = TimeSpan.FromSeconds(0.5),
        });
        clock.Stop();

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12), $"took {clock.Elapsed}");
        Assert.True(LogHas("replace slow"), "no 'replace slow' in log");
    }

    [Fact]
    public async Task NotFound_IsError()
    {
        var url = _server.AddFile("a.bin", TestData.Random(MB, 24), new ServeOptions(FixedStatus: 404));

        var (job, outcome) = await Run(NewState(url), Options());

        Assert.Equal(DownloadStatus.Error, outcome.Status);
        Assert.Equal("Файл не найден на сервере (404)", outcome.Error);
        Assert.Equal(DownloadStatus.Error, job.State.Status);
        Assert.Equal("Файл не найден на сервере (404)", job.State.Error);
    }

    [Fact]
    public async Task Forbidden_FirstProbe_SaysAccessDenied()
    {
        var url = _server.AddFile("a.bin", TestData.Random(MB, 25), new ServeOptions(FixedStatus: 403));

        var (_, outcome) = await Run(NewState(url), Options());

        Assert.Equal(DownloadStatus.Error, outcome.Status);
        Assert.Equal("Доступ запрещён (403)", outcome.Error);
    }

    [Fact]
    public async Task Forbidden_AfterProgress_SaysStale()
    {
        var content = TestData.Random(16 * MB, 26);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4 };

        var (job, first) = await Run(NewState(url), options, cancelAfter: TimeSpan.FromSeconds(1.5));
        Assert.Equal(DownloadStatus.Paused, first.Status);
        Assert.True(job.DownloadedBytes > 0);

        _server.SetOptions(url, new ServeOptions(FixedStatus: 403));
        var second = await job.RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Error, second.Status);
        Assert.Equal("Ссылка устарела — обновите ссылку", second.Error);
    }

    [Fact]
    public async Task FileChanged_RestartsFromZero()
    {
        var original = TestData.Random(16 * MB, 27);
        var url = _server.AddFile("a.bin", original, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4 };

        var (_, first) = await Run(NewState(url), options, cancelAfter: TimeSpan.FromSeconds(1.5));
        Assert.Equal(DownloadStatus.Paused, first.Status);

        var replaced = TestData.Random(16 * MB, 28);   // same size: only the ETag tells the change
        _server.ReplaceContent(url, replaced);
        _server.SetOptions(url, new ServeOptions());
        var job = NewJob(LoadSaved(), options);
        var second = await job.RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, second.Status);
        Assert.Equal(TestData.Sha256(replaced), TestData.Sha256(second.FinalPath!));
        Assert.True(LogHas("restart: file changed"), "no 'restart: file changed' in log");
    }

    [Fact]
    public async Task RangeIgnored_FallsBackSafely()
    {
        var content = TestData.Random(8 * MB, 29);
        var url = _server.AddFile("a.bin", content, new ServeOptions(IgnoreRangeAfterProbe: true));

        var (job, outcome) = await Run(NewState(url), Options());

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.True(LogHas("single-stream"), "no 'single-stream' in log");
        Assert.False(job.State.SupportsRanges);
    }

    [Fact]
    public async Task ResumeFromOlderStateCopy()
    {
        var content = TestData.Random(32 * MB, 30);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4 };   // ~8 s: still running at 5 s
        var state = NewState(url);
        var statePath = Path.Combine(StateFolder, $"{state.Id:D}.json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[]? copy = null;
        var copier = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            copy = await ReadSharedAsync(statePath);
        });
        var (_, first) = await Run(state, options, ct: cts.Token);
        await copier;
        Assert.Equal(DownloadStatus.Paused, first.Status);

        // Simulated crash: the newest JSON is lost, the older copy and the part file (ahead of it) remain.
        var older = JsonSerializer.Deserialize<DownloadState>(copy!, Json)!;
        var latest = LoadSaved();
        Assert.True(older.Segments.Sum(s => s.Position - s.Start) < latest.Segments.Sum(s => s.Position - s.Start),
            "the copy is not older than the final state");

        _server.SetOptions(url, new ServeOptions());
        var job = NewJob(older, options);
        var second = await job.RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, second.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(second.FinalPath!));
    }

    [Fact]
    public async Task GivesUpAfterTimeout()
    {
        var server = await TestFileServer.StartAsync();   // disposed by the stopper
        var content = TestData.Random(16 * MB, 31);
        var url = server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4, GiveUpAfter = TimeSpan.FromSeconds(3) };

        var stopper = Task.Run(async () =>
        {
            var deadline = Stopwatch.StartNew();
            while (!LogHas("start ") && deadline.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(10);
            await Task.Delay(300);   // some data arrives first: the give-up streak starts mid-download
            await server.DisposeAsync();
        });
        var clock = Stopwatch.StartNew();
        var (job, outcome) = await Run(NewState(url), options);
        clock.Stop();
        await stopper;

        Assert.Equal(DownloadStatus.Error, outcome.Status);
        Assert.StartsWith("Сервер недоступен", outcome.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        Assert.True(job.DownloadedBytes > 0);
    }

    [Fact]
    public async Task FolderUnavailable_IsError()
    {
        var url = _server.AddFile("a.bin", TestData.Random(MB, 32));
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "not-a-folder");
        await File.WriteAllBytesAsync(blocker, [1]);
        var folder = Path.Combine(blocker, "dl");   // a file sits where a directory is needed
        var state = NewState(url);
        state.Folder = folder;

        var (_, outcome) = await Run(state, Options());

        Assert.Equal(DownloadStatus.Error, outcome.Status);
        Assert.Equal($"Нет доступа к папке: {folder}", outcome.Error);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    // Fixed 16 connections: these tests depend on the connection count (auto mode has its own tests).
    private EngineOptions Options() => new() { DownloadFolder = DownloadFolder, StateFolder = StateFolder, ConnectionsPerFile = 16 };

    private DownloadState NewState(Uri url) => new()
    {
        Id = Guid.NewGuid(),
        OriginalUrl = url.ToString(),
        CurrentUrl = url.ToString(),
        Folder = DownloadFolder,
        Status = DownloadStatus.Queued,
        AddedAt = DateTimeOffset.UtcNow,
    };

    private DownloadJob NewJob(DownloadState state, EngineOptions options, HostLimits? limits = null) =>
        new(state, options, _http, limits ?? new HostLimits(), new StateStore(options.StateFolder), _log, _ => false);

    private async Task<(DownloadJob Job, JobOutcome Outcome)> Run(
        DownloadState state, EngineOptions options, HostLimits? limits = null,
        TimeSpan? cancelAfter = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.DownloadFolder);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cancelAfter is { } delay)
            cts.CancelAfter(delay);
        var job = NewJob(state, options, limits);
        return (job, await job.RunAsync(cts.Token));
    }

    private DownloadState LoadSaved() => Assert.Single(new StateStore(StateFolder).LoadAll(_log));

    private bool LogHas(string text) => _log.Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal));

    /// <summary>Reads the state file while the job may be replacing it; retries a sharing violation.</summary>
    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(path);
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(10);
            }
        }
    }
}
