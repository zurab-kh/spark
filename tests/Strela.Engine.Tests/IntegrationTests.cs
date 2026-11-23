using Strela.Engine.Integration;

namespace Strela.Engine.Tests;

public sealed class IntegrationTests
{
    [Theory]
    [InlineData("https://ex.com/ubuntu-24.04.iso", true)]
    [InlineData("  http://ex.com/a/B.ZIP?x=1  ", true)]
    [InlineData("https://ex.com/page.html", false)]
    [InlineData("https://ex.com/", false)]
    [InlineData("ftp://ex.com/a.zip", false)]
    [InlineData("see https://ex.com/a.zip", false)]
    [InlineData("https://a.com/a.zip\nhttps://b.com/b.zip", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Detect(string? text, bool expected)
    {
        Assert.Equal(expected, FileUrlDetector.TryDetect(text, out var url));
        Assert.Equal(expected, url is not null);
    }

    [Fact]
    public void Detect_RejectsTooLong()
    {
        var text = "https://ex.com/" + new string('a', 2100) + ".zip";
        Assert.False(FileUrlDetector.TryDetect(text, out _));
    }

    [Fact]
    public void Ipc_SerializeParse_RoundTrip()
    {
        var messages = new[]
        {
            new IpcMessage("https://ex.com/a.zip", "https://ex.com/", new Dictionary<string, string> { ["X-A"] = "1" }, "k=v", "a.zip"),
            new IpcMessage("https://ex.com/b.bin"),
        };

        var parsed = IpcProtocol.Parse(IpcProtocol.Serialize(messages));

        Assert.Equal(2, parsed.Count);
        Assert.Equal("https://ex.com/a.zip", parsed[0].Url);
        Assert.Equal("https://ex.com/", parsed[0].Referer);
        Assert.Equal("1", parsed[0].Headers!["X-A"]);
        Assert.Equal("k=v", parsed[0].Cookies);
        Assert.Equal("a.zip", parsed[0].FileName);
        Assert.Equal("https://ex.com/b.bin", parsed[1].Url);
        Assert.Null(parsed[1].Referer);
    }

    [Fact]
    public void Ipc_EmptyArray_MeansShowWindow()
    {
        Assert.Empty(IpcProtocol.Parse(IpcProtocol.Serialize([])));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"Url\":\"https://ex.com/a.zip\"}")]
    [InlineData("[{\"Url\":\"\"},{\"Referer\":\"x\"}]")]
    public void Ipc_ParseGarbage_Empty(string json)
    {
        Assert.Empty(IpcProtocol.Parse(json));
    }

    [Fact]
    public void Ipc_ToRequest_RejectsNonHttp()
    {
        Assert.Null(new IpcMessage("file:///c:/x").ToRequest());
        Assert.Null(new IpcMessage("relative/x").ToRequest());
        var req = new IpcMessage("https://ex.com/a.zip", "https://ex.com/", new Dictionary<string, string> { ["X"] = "y" }, "c=1", "n.zip").ToRequest();
        Assert.NotNull(req);
        Assert.Equal(new Uri("https://ex.com/a.zip"), req.Url);
        Assert.Equal("https://ex.com/", req.Referer);
        Assert.Equal("y", req.Headers!["X"]);
        Assert.Equal("c=1", req.Cookies);
        Assert.Equal("n.zip", req.FileName);
    }

    [Fact]
    public async Task Ipc_ServerClient_Delivers()
    {
        var pipe = "Strela.Test." + Guid.NewGuid().ToString("N");
        var log = new ListLog();
        await using var server = new IpcServer(pipe, log);
        var tcs = new TaskCompletionSource<IReadOnlyList<IpcMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Received += m => tcs.TrySetResult(m);
        server.Start();

        var sent = await IpcClient.SendAsync(pipe,
            [new IpcMessage("https://ex.com/a.zip"), new IpcMessage("https://ex.com/b.zip", FileName: "b.zip")],
            TimeSpan.FromSeconds(2));

        Assert.True(sent);
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, received.Count);
        Assert.Equal("b.zip", received[1].FileName);
    }

    [Fact]
    public async Task Ipc_Server_SurvivesHandlerExceptionAndServesNextClient()
    {
        var pipe = "Strela.Test." + Guid.NewGuid().ToString("N");
        var log = new ListLog();
        await using var server = new IpcServer(pipe, log);
        var calls = 0;
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Received += m =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
            second.TrySetResult(m.Count);
        };
        server.Start();

        Assert.True(await IpcClient.SendAsync(pipe, [new IpcMessage("https://ex.com/a.zip")], TimeSpan.FromSeconds(2)));
        Assert.True(await IpcClient.SendAsync(pipe, [new IpcMessage("https://ex.com/b.zip")], TimeSpan.FromSeconds(2)));

        Assert.Equal(1, await second.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(log.Contains("Warn", "boom"));
    }

    [Fact]
    public async Task Ipc_ClientNoServer_ReturnsFalse()
    {
        var pipe = "Strela.Test." + Guid.NewGuid().ToString("N");
        Assert.False(await IpcClient.SendAsync(pipe, [], TimeSpan.FromMilliseconds(300)));
    }
}
