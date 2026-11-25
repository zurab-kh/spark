using System.Diagnostics;
using Strela.Engine.Manager;
using Strela.Engine.Video;

namespace Strela.Engine.Tests;

public class VideoLinksTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&list=PLx&index=3&t=42s")]
    [InlineData("http://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?feature=shared")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    public void VideoLinks_AreRecognised(string link)
    {
        Assert.True(VideoLinks.TryGetYouTubeId(new Uri(link), out var id));
        Assert.Equal("dQw4w9WgXcQ", id);
        Assert.True(VideoLinks.TryParse("  " + link + " ", out var canonical));
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", canonical.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/@channel/videos")]
    [InlineData("https://www.youtube.com/playlist?list=PL1234567890")]
    [InlineData("https://www.youtube.com/watch?v=tooShort")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXc!")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/file.mp4")]
    [InlineData("ftp://youtu.be/dQw4w9WgXcQ")]
    public void OtherLinks_AreNotVideos(string link)
    {
        Assert.False(VideoLinks.TryGetYouTubeId(new Uri(link), out _));
        Assert.False(VideoLinks.TryParse(link, out _));
    }

    [Fact]
    public void TryParse_RejectsText()
    {
        Assert.False(VideoLinks.TryParse(null, out _));
        Assert.False(VideoLinks.TryParse("look https://youtu.be/dQw4w9WgXcQ", out _));
    }
}

public class YtDlpTests
{
    private static readonly VideoToolPaths Tools = new("yt-dlp", "/tools/ffmpeg", "/tools/deno");

    [Fact]
    public void Progress_Info_AndFile_LinesAreParsed()
    {
        Assert.True(YtDlp.TryParseProgress("[spark] 137 downloading 1048576 52428800 NA", out var p));
        Assert.Equal(new YtDlp.Progress("137", false, 1048576, 52428800), p);
        Assert.True(YtDlp.TryParseProgress("[spark] 251-drc finished 3000.0 NA 3100.5", out p));
        Assert.Equal(new YtDlp.Progress("251-drc", true, 3000, 3100), p);
        Assert.True(YtDlp.TryParseProgress("[spark] 140 finished NA 777 NA", out p));
        Assert.Equal(new YtDlp.Progress("140", true, null, 777), p);
        Assert.False(YtDlp.TryParseProgress("[download] 50.0% of 10MiB", out _));

        Assert.True(YtDlp.TryParseInfo("[spark-info] 137+140 54525952 Rick Astley - Never Gonna Give You Up", out var info));
        Assert.Equal(new YtDlp.Info("137+140", 54525952, "Rick Astley - Never Gonna Give You Up"), info);
        Assert.True(YtDlp.TryParseInfo("[spark-info] 18 NA Title", out info));
        Assert.Null(info.Size);

        Assert.True(YtDlp.TryParseFile(@"[spark-file] C:\Users\me\Downloads\Видео [dQw4w9WgXcQ].mp4", out var path));
        Assert.Equal(@"C:\Users\me\Downloads\Видео [dQw4w9WgXcQ].mp4", path);
    }

    [Fact]
    public void Arguments_FollowQuality()
    {
        var url = VideoLinks.Canonical("dQw4w9WgXcQ");
        var best = YtDlp.Arguments(url, VideoQuality.Best, "/dl", "/tmp/v", Tools);
        Assert.Equal(url.AbsoluteUri, best[^1]);
        Assert.Contains("res,fps,vcodec:h264,acodec:m4a", best);
        Assert.Contains("mp4", best);
        Assert.Contains("home:/dl", best);
        Assert.Contains("temp:/tmp/v", best);
        Assert.Contains("--no-playlist", best);
        Assert.Equal("utf-8", best[best.IndexOf("--encoding") + 1]);
        Assert.Equal("/tools/ffmpeg", best[best.IndexOf("--ffmpeg-location") + 1]);
        Assert.Equal("deno:/tools/deno", best[best.IndexOf("--js-runtimes") + 1]);

        Assert.Contains("res:1080,fps,vcodec:h264,acodec:m4a", YtDlp.Arguments(url, VideoQuality.P1080, "/dl", "/t", Tools));
        Assert.Contains("res:720,fps,vcodec:h264,acodec:m4a", YtDlp.Arguments(url, VideoQuality.P720, "/dl", "/t", Tools));
        var audio = YtDlp.Arguments(url, VideoQuality.AudioMp3, "/dl", "/t", Tools);
        Assert.Contains("-x", audio);
        Assert.Equal("mp3", audio[audio.IndexOf("--audio-format") + 1]);
        Assert.DoesNotContain("--merge-output-format", audio);
    }

    [Theory]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Private video. Sign in if you've been granted access to this video", "Это приватное видео")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. This video has been removed by the uploader", "Видео недоступно")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm your age. This video may be inappropriate for some users.", "Видео с ограничением по возрасту: нужен вход в аккаунт")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm you’re not a bot", "YouTube просит подтвердить, что вы не робот: попробуйте позже")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Something new broke", "Something new broke")]
    [InlineData(null, "yt-dlp завершился с ошибкой")]
    public void Errors_AreExplained(string? error, string expected) => Assert.Equal(expected, YtDlp.ErrorText(error));

    [Fact]
    public void ChecksumFiles_AreRead()
    {
        const string a = "1111111111111111111111111111111111111111111111111111111111111111";
        const string b = "2222222222222222222222222222222222222222222222222222222222222222";
        var sums = $"{a}  yt-dlp_macos.zip\n{b}  yt-dlp_macos\n{a}  yt-dlp.exe\n";
        Assert.Equal(b, VideoTools.ParseHash(sums, "yt-dlp_macos"));
        Assert.Equal(a, VideoTools.ParseHash(sums, "yt-dlp.exe"));
        Assert.Null(VideoTools.ParseHash(sums, "yt-dlp_linux"));
        // deno: one hash per file, written by PowerShell or shasum.
        Assert.Equal(b, VideoTools.ParseHash($"Algorithm Hash Path\r\n--------- ---- ----\r\nSHA256 {b} D:\\a\\deno.zip\r\n", "deno-x86_64-pc-windows-msvc.zip"));
        Assert.Equal(a, VideoTools.ParseHash($"{a} *deno-aarch64-apple-darwin.zip", "deno-aarch64-apple-darwin.zip"));
    }
}

/// <summary>YouTube downloads through the manager, with yt-dlp replaced by a script (no network, no tools).</summary>
public sealed class VideoManagerTests : IAsyncLifetime
{
    private const string VideoId = "abcdefghijk";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "strela-tests", "video-" + Guid.NewGuid().ToString("N"));
    private readonly List<DownloadManager> _managers = [];
    private readonly List<IReadOnlyList<string>> _calls = [];

    private string DownloadFolder => Path.Combine(_root, "dl");
    private string StateFolder => Path.Combine(_root, "state");

    /// <summary>What the fake yt-dlp does: (args, stdout, stderr, ct) → exit code.</summary>
    private Func<IReadOnlyList<string>, Action<string>, Action<string>, CancellationToken, Task<int>> _script = SucceedAsync;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
            await manager.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task YouTubeLink_DownloadsMergedVideo()
    {
        var manager = await NewManager();
        var id = manager.Add(new DownloadRequest(new Uri($"https://youtu.be/{VideoId}?si=share")));

        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed)).Single();
        Assert.Equal(id, snap.Id);
        Assert.Equal(DownloadKind.Video, snap.Kind);
        Assert.Equal($"https://www.youtube.com/watch?v={VideoId}", snap.Url);
        Assert.Equal($"Test Video [{VideoId}].mp4", snap.FileName);
        Assert.Equal(Path.Combine(DownloadFolder, snap.FileName), snap.FilePath);
        Assert.Equal(2500, snap.TotalBytes);
        Assert.Equal(2500, snap.DownloadedBytes);
        Assert.False(Directory.Exists(Path.Combine(StateFolder, "video", id.ToString("N"))));

        var args = Assert.Single(_calls);
        Assert.Equal($"https://www.youtube.com/watch?v={VideoId}", args[^1]);
        Assert.Contains("res,fps,vcodec:h264,acodec:m4a", args);
    }

    [Fact]
    public async Task GarbledFilePath_FileIsFoundByVideoId()
    {
        _script = async (args, stdout, _, ct) =>
        {
            await Task.Yield();
            stdout("[spark-info] 18 2500 ????? 5");
            var path = Path.Combine(Arg(args, "home:"), $"Мстители 5 [{VideoId}].mp4");
            await File.WriteAllBytesAsync(path + ".part", new byte[10], ct);   // leftovers are not the video
            await File.WriteAllBytesAsync(path, new byte[2500], ct);
            stdout($"[spark-file] {Path.Combine(Arg(args, "home:"), $"?????? 5 [{VideoId}].mp4")}");
            return 0;
        };
        var manager = await NewManager();
        manager.Add(new DownloadRequest(new Uri($"https://youtu.be/{VideoId}")));

        var snap = (await WaitFor(manager, s => s.Single().Status is DownloadStatus.Completed or DownloadStatus.Error)).Single();
        Assert.Equal(DownloadStatus.Completed, snap.Status);
        Assert.Equal($"Мстители 5 [{VideoId}].mp4", snap.FileName);
        Assert.Equal(2500, snap.TotalBytes);
    }

    [Fact]
    public async Task SameVideo_IsNotAddedTwice()
    {
        _script = BlockAsync;
        var manager = await NewManager();
        var first = manager.Add(new DownloadRequest(new Uri($"https://www.youtube.com/watch?v={VideoId}&t=10")));
        var again = manager.Add(new DownloadRequest(new Uri($"https://m.youtube.com/shorts/{VideoId}")));
        Assert.Equal(first, again);
        Assert.Single(manager.GetSnapshots());
    }

    [Fact]
    public async Task Quality_IsTakenWhenAdded_AndKeptForResume()
    {
        _script = BlockAsync;
        var manager = await NewManager(VideoQuality.P720);
        var id = manager.Add(new DownloadRequest(new Uri($"https://youtu.be/{VideoId}")));
        await WaitFor(manager, s => s.Single().DownloadedBytes == 1000);

        manager.Pause(id);
        await WaitFor(manager, s => s.Single().Status == DownloadStatus.Paused);
        manager.ApplyOptions(Options(VideoQuality.Best));
        manager.Resume(id);
        await Until(() => _calls.Count == 2, "no second run");

        Assert.All(_calls, args => Assert.Contains("res:720,fps,vcodec:h264,acodec:m4a", args));
    }

    [Fact]
    public async Task Pause_KillsYtDlp_KeepsProgress_AndResumeContinues()
    {
        _script = BlockAsync;
        var manager = await NewManager();
        var id = manager.Add(new DownloadRequest(new Uri($"https://youtu.be/{VideoId}")));
        var running = (await WaitFor(manager, s => s.Single().DownloadedBytes == 1000)).Single();
        Assert.Equal("Test Video", running.FileName);
        Assert.Equal(3000, running.TotalBytes);

        manager.Pause(id);
        var paused = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Paused)).Single();
        Assert.Equal(1000, paused.DownloadedBytes);

        _script = SucceedAsync;
        manager.Resume(id);
        await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task YtDlpError_EndsInExplainedError()
    {
        _script = async (_, _, stderr, _) =>
        {
            await Task.Yield();
            stderr("WARNING: [youtube] something harmless");
            stderr($"ERROR: [youtube] {VideoId}: Private video. Sign in if you've been granted access to this video");
            return 1;
        };
        var manager = await NewManager();
        manager.Add(new DownloadRequest(new Uri($"https://youtu.be/{VideoId}")));

        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Error)).Single();
        Assert.Equal("Это приватное видео", snap.StatusText);
    }

    [Fact]
    public async Task RemoveWithFile_DeletesVideo_AndTempFolder()
    {
        var manager = await NewManager();
        var id = manager.Add(new DownloadRequest(new Uri($"https://youtu.be/{VideoId}")));
        var snap = (await WaitFor(manager, s => s.Single().Status == DownloadStatus.Completed)).Single();
        Directory.CreateDirectory(Path.Combine(StateFolder, "video", id.ToString("N")));

        await manager.RemoveAsync(id, deleteFile: true);

        Assert.False(File.Exists(snap.FilePath));
        Assert.False(Directory.Exists(Path.Combine(StateFolder, "video", id.ToString("N"))));
        Assert.Empty(manager.GetSnapshots());
    }

    [Fact]
    public async Task CompletedVideo_IsThereAfterRestart()
    {
        var first = await NewManager();
        first.Add(new DownloadRequest(new Uri($"https://youtu.be/{VideoId}")));
        var done = (await WaitFor(first, s => s.Single().Status == DownloadStatus.Completed)).Single();
        await first.DisposeAsync();
        _managers.Remove(first);

        var second = await NewManager();
        var loaded = second.GetSnapshots().Single();
        Assert.Equal(DownloadKind.Video, loaded.Kind);
        Assert.Equal(DownloadStatus.Completed, loaded.Status);
        Assert.Equal(done.FilePath, loaded.FilePath);
        Assert.Equal(2500, loaded.DownloadedBytes);
        // Done and the file is still there: the same link is not downloaded again.
        Assert.Equal(loaded.Id, second.Add(new DownloadRequest(new Uri($"https://www.youtube.com/watch?v={VideoId}"))));
    }

    // ---- fake yt-dlp --------------------------------------------------------------------------------------

    private static string Arg(IReadOnlyList<string> args, string prefix) =>
        args.First(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    /// <summary>Video and audio streams one after the other, then the merged file in the download folder.</summary>
    private static async Task<int> SucceedAsync(IReadOnlyList<string> args, Action<string> stdout, Action<string> stderr, CancellationToken ct)
    {
        await Task.Yield();
        Assert.True(Directory.Exists(Arg(args, "temp:")));
        stdout("[spark-info] 137+140 3000 Test Video");
        stdout("[spark] 137 downloading 1000 2000 NA");
        stdout("[spark] 137 finished 2000 2000 NA");
        stdout("[spark] 140 downloading 500 NA 1000");
        stdout("[spark] 140 finished 1000 1000 NA");
        var path = Path.Combine(Arg(args, "home:"), $"Test Video [{VideoId}].mp4");
        await File.WriteAllBytesAsync(path, new byte[2500], ct);
        stdout($"[spark-file] {path}");
        return 0;
    }

    /// <summary>Part of the video stream, then waits until killed (cancelled), like the real runner.</summary>
    private static async Task<int> BlockAsync(IReadOnlyList<string> args, Action<string> stdout, Action<string> stderr, CancellationToken ct)
    {
        await Task.Yield();
        stdout("[spark-info] 137+140 3000 Test Video");
        stdout("[spark] 137 downloading 1000 2000 NA");
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
        return 0;
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private EngineOptions Options(VideoQuality quality = VideoQuality.Best) => new()
    {
        DownloadFolder = DownloadFolder,
        StateFolder = StateFolder,
        VideoQuality = quality,
        VideoToolsOverride = new VideoToolPaths("yt-dlp", null, null),
        VideoRunner = (exe, args, stdout, stderr, ct) =>
        {
            lock (_calls)
                _calls.Add(args);
            return _script(args, stdout, stderr, ct);
        },
    };

    private async Task<DownloadManager> NewManager(VideoQuality quality = VideoQuality.Best)
    {
        var manager = new DownloadManager(Options(quality), NullLog.Instance);
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
            await Task.Delay(20);
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Timeout)
                throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }
}
