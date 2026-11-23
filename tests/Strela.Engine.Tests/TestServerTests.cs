using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Strela.TestServer;

namespace Strela.Engine.Tests;

public class TestServerTests
{
    [Fact]
    public async Task Range_Returns206WithExactBytes()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(1000, 1);
        var url = server.AddFile("a.bin", content);
        using var http = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(content[10..20], body);
        Assert.Equal("bytes 10-19/1000", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.NotNull(response.Headers.ETag);
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task NoRangeSupport_Returns200Full()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(1000, 2);
        var url = server.AddFile("a.bin", content, new ServeOptions(SupportsRanges: false));
        using var http = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.AcceptRanges);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(content.Length, body.Length);
    }

    [Fact]
    public async Task RateLimit_IsRespected()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(1_000_000, 3);
        var url = server.AddFile("a.bin", content, new ServeOptions(BytesPerSecondPerConnection: 500_000));
        using var http = new HttpClient();

        var sw = Stopwatch.StartNew();
        var body = await http.GetByteArrayAsync(url);
        sw.Stop();

        Assert.Equal(content.Length, body.Length);
        Assert.True(sw.Elapsed.TotalSeconds >= 1.8, $"took {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task MaxConcurrent_Returns503()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(200_000, 4);
        var url = server.AddFile("a.bin", content,
            new ServeOptions(BytesPerSecondPerConnection: 100_000, MaxConcurrent: 1));
        using var http = new HttpClient();

        using var first = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using var firstBody = await first.Content.ReadAsStreamAsync();
        var one = new byte[1024];
        Assert.True(await firstBody.ReadAsync(one) > 0);

        using var second = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), second.Headers.RetryAfter!.Delta);
        Assert.Equal(1, server.MaxConcurrentSeen(url));
    }

    [Fact]
    public async Task DropAfterBytes_AbortsConnectionMidBody()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(1_000_000, 5);
        var url = server.AddFile("a.bin", content, new ServeOptions(DropAfterBytes: 30_000));
        using var http = new HttpClient();

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync();

        long received = 0;
        var buffer = new byte[8192];
        var failure = await Record.ExceptionAsync(async () =>
        {
            int n;
            while ((n = await body.ReadAsync(buffer)) > 0)
            {
                received += n;
            }
        });

        Assert.NotNull(failure);
        // Abort() может потерять уже отправленные байты: N — верхняя граница, а не точное число.
        Assert.InRange(received, 1, 30_000);
    }

    [Fact]
    public async Task SlowConnectionCount_ThrottlesOnlyFirstConnections()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(100_000, 6);
        var url = server.AddFile("a.bin", content,
            new ServeOptions(SlowConnectionCount: 1, SlowBytesPerSecond: 50_000));
        using var http = new HttpClient();

        var sw = Stopwatch.StartNew();
        await http.GetByteArrayAsync(url);
        var slow = sw.Elapsed;
        sw.Restart();
        await http.GetByteArrayAsync(url);
        var fast = sw.Elapsed;

        Assert.True(slow.TotalSeconds >= 1.5, $"slow took {slow.TotalSeconds:F2}s");
        Assert.True(fast.TotalSeconds < 1.0, $"fast took {fast.TotalSeconds:F2}s");
        Assert.Equal(2, server.ConnectionsOpened(url));
    }

    [Fact]
    public async Task FixedStatus_ReturnsThatStatus()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(100, 7), new ServeOptions(FixedStatus: 404));
        using var http = new HttpClient();

        using var response = await http.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IgnoreRangeAfterProbe_ProbeGets206_OtherRangesGetFullBody()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(1000, 8);
        var url = server.AddFile("a.bin", content, new ServeOptions(IgnoreRangeAfterProbe: true));
        using var http = new HttpClient();

        using var probe = new HttpRequestMessage(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, 0);
        using var probeResponse = await http.SendAsync(probe);
        Assert.Equal(HttpStatusCode.PartialContent, probeResponse.StatusCode);
        Assert.Equal("bytes 0-0/1000", probeResponse.Content.Headers.ContentRange!.ToString());

        using var real = new HttpRequestMessage(HttpMethod.Get, url);
        real.Headers.Range = new RangeHeaderValue(100, 199);
        using var realResponse = await http.SendAsync(real);
        Assert.Equal(HttpStatusCode.OK, realResponse.StatusCode);
        Assert.Equal(1000, (await realResponse.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task OmitContentLength_UsesChunkedEncoding()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(50_000, 9);
        var url = server.AddFile("a.bin", content, new ServeOptions(OmitContentLength: true));
        using var http = new HttpClient();

        // HttpClient проставляет ContentLength после полной буферизации, поэтому читаем только заголовки.
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        Assert.Null(response.Content.Headers.ContentLength);
        Assert.True(response.Headers.TransferEncodingChunked);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task OpenEndedRange_AndUnsatisfiableRange()
    {
        await using var server = await TestFileServer.StartAsync();
        var content = TestData.Random(1000, 10);
        var url = server.AddFile("a.bin", content);
        using var http = new HttpClient();

        using var open = new HttpRequestMessage(HttpMethod.Get, url);
        open.Headers.Range = new RangeHeaderValue(990, null);
        using var openResponse = await http.SendAsync(open);
        Assert.Equal(HttpStatusCode.PartialContent, openResponse.StatusCode);
        Assert.Equal(content[990..], await openResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 990-999/1000", openResponse.Content.Headers.ContentRange!.ToString());

        using var bad = new HttpRequestMessage(HttpMethod.Get, url);
        bad.Headers.Range = new RangeHeaderValue(5000, 6000);
        using var badResponse = await http.SendAsync(bad);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, badResponse.StatusCode);
    }

    [Fact]
    public async Task ReplaceContent_ChangesETagAndBody()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(100, 11));
        using var http = new HttpClient();
        using var before = await http.GetAsync(url);
        var etagBefore = before.Headers.ETag;

        var replacement = TestData.Random(150, 12);
        server.ReplaceContent(url, replacement);
        using var after = await http.GetAsync(url);

        Assert.NotEqual(etagBefore, after.Headers.ETag);
        Assert.Equal(replacement, await after.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HeadRequest_ReturnsHeadersWithoutBody()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(1000, 13),
            new ServeOptions(ContentDisposition: "attachment; filename=\"x.bin\""));
        using var http = new HttpClient();

        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1000, response.Content.Headers.ContentLength);
        Assert.Equal("x.bin", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }
}
