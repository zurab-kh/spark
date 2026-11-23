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

public sealed class DownloadJobTests : IAsyncLifetime
{
    private const int MB = 1024 * 1024;
    private const long OneMBps = 1_000_000;

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
    public async Task Downloads_ExactContent_16Connections()
    {
        var content = TestData.Random(20 * MB, 1);
        var url = _server.AddFile("a.bin", content);

        var (job, outcome) = await RunJob(url);

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(Path.Combine(DownloadFolder, "a.bin"), outcome.FinalPath);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.False(File.Exists(outcome.FinalPath + ".strela"));
        Assert.Equal(DownloadStatus.Completed, job.State.Status);
        Assert.NotNull(job.State.CompletedAt);
        Assert.Equal(content.Length, job.DownloadedBytes);
    }

    [Fact]
    public async Task PerConnectionLimit_UsesManyConnections()
    {
        var content = TestData.Random(32 * MB, 2);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));

        var clock = Stopwatch.StartNew();
        var (_, outcome) = await RunJob(url);
        clock.Stop();

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"took {clock.Elapsed}");
        Assert.True(_server.ConnectionsOpened(url) >= 16, $"connections {_server.ConnectionsOpened(url)}");
    }

    [Fact]
    public async Task NoTail_WorkStealingKicksIn()
    {
        // With every connection at exactly 1 MB/s the four equal segments finish together and nothing is left
        // to steal. So only the first data connection is limited to 1 MB/s (connection 1 is the probe), the
        // rest are unlimited: without stealing that one 8 MB segment would be an ~8.4 s tail.
        var content = TestData.Random(32 * MB, 3);
        var url = _server.AddFile("a.bin", content,
            new ServeOptions(SlowConnectionCount: 2, SlowBytesPerSecond: OneMBps));

        var clock = Stopwatch.StartNew();
        var (_, outcome) = await RunJob(url, Options() with { ConnectionsPerFile = 4 });
        clock.Stop();

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(11), $"took {clock.Elapsed}");
        Assert.True(_log.Contains("Info", "split"), "no split in log");
    }

    [Fact]
    public async Task SingleStream_WhenNoRanges()
    {
        var content = TestData.Random(3 * MB, 4);
        var url = _server.AddFile("a.bin", content, new ServeOptions(SupportsRanges: false));

        var (_, outcome) = await RunJob(url);

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.True(_log.Contains("Info", "single-stream"), "no single-stream in log");
    }

    [Fact]
    public async Task SingleStream_UnknownLength()
    {
        var content = TestData.Random(3 * MB + 12345, 5);
        var url = _server.AddFile("a.bin", content, new ServeOptions(SupportsRanges: false, OmitContentLength: true));

        var (job, outcome) = await RunJob(url);

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(outcome.FinalPath!));
        Assert.Equal(content.Length, job.State.TotalBytes);
    }

    [Fact]
    public async Task ZeroByteFile()
    {
        var url = _server.AddFile("empty.bin", []);

        var (_, outcome) = await RunJob(url);

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(0, new FileInfo(outcome.FinalPath!).Length);
        Assert.False(File.Exists(outcome.FinalPath + ".strela"));
    }

    [Fact]
    public async Task Cancel_SavesStateNotAheadOfData()
    {
        var content = TestData.Random(32 * MB, 6);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));

        // 4 connections: the download takes ~8 s, so it is still running when cancelled at 3 s.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var (job, outcome) = await RunJob(url, Options() with { ConnectionsPerFile = 4 }, cts.Token);

        Assert.Equal(DownloadStatus.Paused, outcome.Status);
        Assert.Null(outcome.FinalPath);

        var json = File.ReadAllBytes(Path.Combine(StateFolder, $"{job.State.Id:D}.json"));
        var saved = JsonSerializer.Deserialize<DownloadState>(json, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
        Assert.Equal(DownloadStatus.Paused, saved.Status);
        Assert.NotEmpty(saved.Segments);

        var part = File.ReadAllBytes(saved.PartPath!);
        long written = 0;
        foreach (var s in saved.Segments)
        {
            var length = (int)(s.Position - s.Start);
            Assert.True(part.AsSpan((int)s.Start, length).SequenceEqual(content.AsSpan((int)s.Start, length)),
                $"segment [{s.Start}, {s.Position}) differs from the original");
            written += length;
        }

        Assert.InRange(written, 1, content.Length - 1);

        // The saved segments tile [0, total) exactly — a stolen tail is never lost.
        long cursor = 0;
        foreach (var s in saved.Segments.OrderBy(s => s.Start))
        {
            Assert.Equal(cursor, s.Start);
            cursor = s.End;
        }
        Assert.Equal(content.Length, cursor);
    }

    [Fact]
    public async Task IsPathTaken_IgnoresTheJobsOwnPaths()
    {
        var content = TestData.Random(3 * MB, 7);
        var url = _server.AddFile("a.bin", content);

        // Like a manager that reports the paths of every active job — including this one — as taken.
        var (_, outcome) = await RunJob(url, isTaken: (state, path) =>
            state.FileName is { } name && (path == Path.Combine(state.Folder, name) || path == state.PartPath));

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.Equal(Path.Combine(DownloadFolder, "a.bin"), outcome.FinalPath);
    }

    [Fact]
    public async Task Resume_AfterPause_CompletesWithExactContent()
    {
        var content = TestData.Random(32 * MB, 8);
        var url = _server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: OneMBps));
        var options = Options() with { ConnectionsPerFile = 4 };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (paused, first) = await RunJob(url, options, cts.Token);
        Assert.Equal(DownloadStatus.Paused, first.Status);
        var saved = Assert.Single(new StateStore(StateFolder).LoadAll(_log));
        var savedBytes = saved.Segments.Sum(s => s.Position - s.Start);
        Assert.InRange(savedBytes, 1, content.Length - 1);

        _server.SetOptions(url, new ServeOptions());
        var job = new DownloadJob(saved, options, _http, new HostLimits(), new StateStore(StateFolder), _log, _ => false);
        Assert.Equal(paused.State.Id, job.State.Id);
        var second = await job.RunAsync(CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, second.Status);
        Assert.Equal(Path.Combine(DownloadFolder, "a.bin"), second.FinalPath);
        Assert.Equal(TestData.Sha256(content), TestData.Sha256(second.FinalPath!));
        Assert.Equal(content.Length, job.DownloadedBytes);
    }

    private EngineOptions Options() => new() { DownloadFolder = DownloadFolder, StateFolder = StateFolder };

    private async Task<(DownloadJob Job, JobOutcome Outcome)> RunJob(
        Uri url, EngineOptions? o = null, CancellationToken ct = default, Func<DownloadState, string, bool>? isTaken = null)
    {
        o ??= Options();
        Directory.CreateDirectory(o.DownloadFolder);
        var state = new DownloadState
        {
            Id = Guid.NewGuid(),
            OriginalUrl = url.ToString(),
            CurrentUrl = url.ToString(),
            Folder = o.DownloadFolder,
            Status = DownloadStatus.Queued,
            AddedAt = DateTimeOffset.UtcNow,
        };

        var job = new DownloadJob(state, o, _http, new HostLimits(), new StateStore(o.StateFolder), _log,
            path => isTaken?.Invoke(state, path) ?? false);
        var outcome = await job.RunAsync(ct);
        return (job, outcome);
    }
}
