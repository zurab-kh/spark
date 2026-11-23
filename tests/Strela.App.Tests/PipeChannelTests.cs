using Strela.App.Services;

namespace Strela.App.Tests;

public class PipeChannelTests
{
    [Fact]
    public async Task Send_DeliversUrls_ToServer()
    {
        var name = "Strela.Test." + Guid.NewGuid().ToString("N");
        using var server = new PipeSingleInstanceChannel(name);
        var got = new TaskCompletionSource<IReadOnlyList<string>>();
        server.Received += urls => got.TrySetResult(urls);
        server.Start();

        using var client = new PipeSingleInstanceChannel(name);
        Assert.True(await client.SendAsync(new[] { "https://a/x.zip", "http://b/y.iso" }));

        var urls = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "https://a/x.zip", "http://b/y.iso" }, urls);
    }

    [Fact]
    public async Task Send_NoServer_ReturnsFalse()
    {
        using var client = new PipeSingleInstanceChannel("Strela.Test." + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(300));
        Assert.False(await client.SendAsync(Array.Empty<string>()));
    }
}
