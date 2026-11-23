using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Strela.App.Services;
using Strela.Engine;
using Strela.Engine.Integration;

namespace Strela.App.Tests;

public class NativeMessagingTests
{
    private static string NewPipe() => "Spark.Test." + Guid.NewGuid().ToString("N");

    private static IpcServer StartServer(string pipe, out Task<IReadOnlyList<IpcMessage>> first)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<IpcMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new IpcServer(pipe, NullLog.Instance);
        server.Received += m => tcs.TrySetResult(m);
        server.Start();
        first = tcs.Task;
        return server;
    }

    private static NativeMessagingHost Host(string pipe, bool enabled = true, Func<bool>? launch = null,
        TimeSpan? startWait = null) =>
        new(() => enabled, NullLog.Instance, pipe, launch ?? (() => false), startWait ?? TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(150));

    [Fact]
    public async Task Frames_RoundTrip_Utf8AndLittleEndianLength()
    {
        var ms = new MemoryStream();
        const string json = "{\"fileName\":\"файл №1.zip\"}";
        await NativeMessagingFrames.WriteAsync(ms, json);

        var bytes = ms.ToArray();
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(json), BinaryPrimitives.ReadUInt32LittleEndian(bytes));

        ms.Position = 0;
        Assert.Equal(json, await NativeMessagingFrames.ReadAsync(ms));
        Assert.Null(await NativeMessagingFrames.ReadAsync(ms));   // чистый конец потока
    }

    [Fact]
    public async Task Frames_TruncatedBody_Throws()
    {
        var ms = new MemoryStream(new byte[] { 10, 0, 0, 0, (byte)'{' });
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessagingFrames.ReadAsync(ms));
    }

    [Fact]
    public async Task Frames_OversizedLength_Throws()
    {
        var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessagingFrames.ReadAsync(ms));
    }

    [Fact]
    public async Task Host_ForwardsAllFields_ToIpcServer_AndRepliesOk()
    {
        var pipe = NewPipe();
        await using var server = StartServer(pipe, out var received);

        var reply = await Host(pipe).HandleAsync("""
            {"url":"https://example.com/big.iso","referer":"https://example.com/page","fileName":"big.iso",
             "cookies":"a=1; b=2","headers":{"User-Agent":"UA/1"}}
            """);

        Assert.Equal("""{"ok":true}""", reply);
        var message = Assert.Single(await received.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("https://example.com/big.iso", message.Url);
        Assert.Equal("https://example.com/page", message.Referer);
        Assert.Equal("big.iso", message.FileName);
        Assert.Equal("a=1; b=2", message.Cookies);
        Assert.Equal("UA/1", message.Headers!["User-Agent"]);
    }

    [Fact]
    public async Task Host_RunAsync_ReadsFramesAndWritesFramedReplies()
    {
        var pipe = NewPipe();
        await using var server = StartServer(pipe, out var received);

        var input = new MemoryStream();
        await NativeMessagingFrames.WriteAsync(input, """{"type":"ping"}""");
        await NativeMessagingFrames.WriteAsync(input, """{"url":"http://h/x.zip"}""");
        input.Position = 0;
        var output = new MemoryStream();

        await Host(pipe).RunAsync(input, output);

        output.Position = 0;
        Assert.Equal("""{"ok":true,"enabled":true}""", await NativeMessagingFrames.ReadAsync(output));
        Assert.Equal("""{"ok":true}""", await NativeMessagingFrames.ReadAsync(output));
        Assert.Null(await NativeMessagingFrames.ReadAsync(output));
        Assert.Equal("http://h/x.zip", Assert.Single(await received.WaitAsync(TimeSpan.FromSeconds(3))).Url);
    }

    [Fact]
    public async Task Host_Disabled_RepliesNotOk_AndSendsNothing()
    {
        var pipe = NewPipe();
        await using var server = StartServer(pipe, out var received);

        var reply = await Host(pipe, enabled: false).HandleAsync("""{"url":"https://example.com/big.iso"}""");

        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        await Task.Delay(300);
        Assert.False(received.IsCompleted);
    }

    [Fact]
    public async Task Host_Ping_ReportsEnabledState()
    {
        Assert.Equal("""{"ok":true,"enabled":false}""", await Host(NewPipe(), enabled: false).HandleAsync("""{"type":"ping"}"""));
    }

    [Theory]
    [InlineData("""{"url":"ftp://h/x"}""")]
    [InlineData("""{"referer":"https://h/"}""")]
    [InlineData("[1,2]")]
    [InlineData("не json")]
    public async Task Host_BadMessage_RepliesNotOk(string json)
    {
        var reply = await Host(NewPipe()).HandleAsync(json);
        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Host_SparkNotRunning_LaunchesItOnceAndRetriesUntilServerAppears()
    {
        var pipe = NewPipe();
        IpcServer? server = null;
        var launches = 0;
        Task<IReadOnlyList<IpcMessage>>? received = null;
        var host = Host(pipe, launch: () =>
        {
            launches++;
            server = StartServer(pipe, out received);   // Spark поднялся
            return true;
        }, startWait: TimeSpan.FromSeconds(3));

        var reply = await host.HandleAsync("""{"url":"https://example.com/a.zip"}""");

        Assert.Equal("""{"ok":true}""", reply);
        Assert.Equal(1, launches);
        Assert.Equal("https://example.com/a.zip", Assert.Single(await received!.WaitAsync(TimeSpan.FromSeconds(3))).Url);
        await server!.DisposeAsync();
    }

    [Fact]
    public async Task Host_SparkNeverStarts_RepliesNotOkAfterWait()
    {
        var launches = 0;
        var host = Host(NewPipe(), launch: () => { launches++; return true; }, startWait: TimeSpan.FromMilliseconds(600));

        var reply = await host.HandleAsync("""{"url":"https://example.com/a.zip"}""");

        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, launches);
    }

    [Theory]
    [InlineData("--native-host", true)]
    [InlineData("chrome-extension://meffaihaokjhicodaannmgedjflgbcgd/", true)]
    [InlineData("https://example.com/x.zip", false)]
    public void IsNativeHostLaunch_DetectsBrowserAndFlag(string arg, bool expected) =>
        Assert.Equal(expected, NativeMessagingHost.IsNativeHostLaunch(new[] { arg, "--parent-window=0" }));

    [Fact]
    public void Settings_InterceptDefaultsOn_AndSurvivesStore()
    {
        Assert.True(new AppSettings().InterceptBrowserDownloads);
        var path = Path.Combine(Path.GetTempPath(), "spark-test-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { InterceptBrowserDownloads = false });
            Assert.False(store.Load().InterceptBrowserDownloads);
            File.WriteAllText(path, "{\"DownloadFolder\":\"\"}");   // старый файл без нового поля
            Assert.True(store.Load().InterceptBrowserDownloads);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task EndToEnd_BrowserMessage_ReachesViewModelWithAllFields()
    {
        var pipe = NewPipe();
        using var channel = new PipeSingleInstanceChannel(pipe);
        var fake = new FakeDownloadManager();
        var vm = new Strela.App.ViewModels.MainViewModel(fake);
        var done = new TaskCompletionSource();
        channel.MessagesReceived += m => { vm.AddRequests(m); done.TrySetResult(); };
        channel.Start();

        var reply = await Host(pipe).HandleAsync(
            """{"url":"https://example.com/f.bin","referer":"https://example.com/","cookies":"s=1","fileName":"f.bin"}""");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("""{"ok":true}""", reply);
        var request = Assert.Single(fake.Added);
        Assert.Equal("https://example.com/", request.Referer);
        Assert.Equal("s=1", request.Cookies);
        Assert.Equal("f.bin", request.FileName);
    }
}
