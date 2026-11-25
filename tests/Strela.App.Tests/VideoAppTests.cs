using Strela.App.Services;
using Strela.App.ViewModels;
using Strela.Engine;
using Strela.Engine.Integration;
using Strela.Engine.Manager;
using Strela.Engine.Video;

namespace Strela.App.Tests;

public sealed class VideoAppTests : IDisposable
{
    private const string Link = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const long MB = 1024L * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spark-video-" + Guid.NewGuid().ToString("N"));

    public VideoAppTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void AddLinks_PassesYouTubeLinksToTheEngine()
    {
        var fake = new FakeDownloadManager();
        var vm = new MainViewModel(fake);
        Assert.Equal(2, vm.AddLinks("https://youtu.be/dQw4w9WgXcQ\nhttps://www.youtube.com/shorts/aqz-KE-bpKQ"));
        Assert.Equal(["https://youtu.be/dQw4w9WgXcQ", "https://www.youtube.com/shorts/aqz-KE-bpKQ"],
            fake.Added.Select(r => r.Url.AbsoluteUri));
    }

    [Fact]
    public void VideoRow_ShowsYtBadge_ThenTheFileType()
    {
        var vm = new DownloadItemViewModel(Guid.NewGuid());
        vm.Update(new DownloadSnapshot(vm.Id, "Rick Astley - Never Gonna Give You Up", null, Link, 60 * MB, 20 * MB, 8 * MB,
            TimeSpan.FromSeconds(5), DownloadStatus.Downloading, null, 0, 0, DownloadKind.Video));
        Assert.Equal("YT", vm.TypeLabel);
        Assert.Equal("media", vm.BadgeKind);
        Assert.Equal($"{Format.Bytes(20 * MB)} из {Format.Bytes(60 * MB)} · {Format.Speed(8 * MB)} · осталось {Format.Eta(TimeSpan.FromSeconds(5))}",
            vm.DetailText);

        vm.Update(new DownloadSnapshot(vm.Id, "Rick Astley - Never Gonna Give You Up [dQw4w9WgXcQ].mp4", @"C:\dl\x.mp4", Link,
            60 * MB, 60 * MB, 0, null, DownloadStatus.Completed, null, 0, 0, DownloadKind.Video));
        Assert.Equal("MP4", vm.TypeLabel);
        Assert.Equal("media", vm.BadgeKind);
    }

    [Fact]
    public void VideoRow_ShowsPreparationStatus()
    {
        var vm = new DownloadItemViewModel(Guid.NewGuid());
        vm.Update(new DownloadSnapshot(vm.Id, "Видео YouTube dQw4w9WgXcQ", null, Link, null, 0, 0, null,
            DownloadStatus.Downloading, "Подготовка YouTube: ffmpeg 45%", 0, 0, DownloadKind.Video));
        Assert.EndsWith("Подготовка YouTube: ffmpeg 45%", vm.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoQuality_IsSaved_AndReachesTheEngine()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { VideoQuality = VideoQuality.AudioMp3 });
        var loaded = store.Load();
        Assert.Equal(VideoQuality.AudioMp3, loaded.VideoQuality);
        Assert.Equal(VideoQuality.AudioMp3, loaded.ToEngineOptions(_dir).VideoQuality);

        // Old settings file: the best quality; a broken value too.
        File.WriteAllText(path, """{ "MaxActiveDownloads": 4, "SettingsVersion": 2 }""");
        Assert.Equal(VideoQuality.Best, new SettingsStore(path).Load().VideoQuality);
        Assert.Equal(VideoQuality.Best, new AppSettings { VideoQuality = (VideoQuality)42 }.Clamped().VideoQuality);
    }

    [Fact]
    public async Task ExtensionButton_GoesThrough_EvenWithInterceptionOff()
    {
        var pipe = "Spark.Test." + Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IReadOnlyList<IpcMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new IpcServer(pipe, NullLog.Instance);
        server.Received += m => tcs.TrySetResult(m);
        server.Start();
        var now = DateTimeOffset.Parse("2026-11-23T19:00:00Z");
        var host = new NativeMessagingHost(() => false, NullLog.Instance, pipe, () => false, TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(150), () => now);

        var reply = await host.HandleAsync($$"""{"v":2,"explicit":true,"startTime":"2026-11-23T18:59:59.5Z","url":"{{Link}}"}""");

        Assert.Equal("""{"ok":true}""", reply);
        Assert.Equal(Link, Assert.Single(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3))).Url);
    }
}
