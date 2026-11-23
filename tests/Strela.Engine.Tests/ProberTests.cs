using System.Net;
using Strela.Engine.Http;
using Strela.Engine.Probe;
using Strela.TestServer;

namespace Strela.Engine.Tests;

public class ProberTests
{
    [Fact]
    public async Task Probe_RangeServer()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(5000, 1));
        using var http = StrelaHttp.Create();

        var result = await Prober.ProbeAsync(http, new DownloadRequest(url), CancellationToken.None);

        Assert.Equal(5000, result.TotalBytes);
        Assert.True(result.SupportsRanges);
        Assert.NotNull(result.ETag);
        Assert.NotNull(result.LastModified);
        Assert.Equal("a.bin", result.FileName);
        Assert.Equal(HttpStatusCode.PartialContent, result.Status);
        Assert.Equal(url, result.FinalUri);
    }

    [Fact]
    public async Task Probe_NoRangeServer()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(5000, 2), new ServeOptions(SupportsRanges: false));
        using var http = StrelaHttp.Create();

        var result = await Prober.ProbeAsync(http, new DownloadRequest(url), CancellationToken.None);

        Assert.False(result.SupportsRanges);
        Assert.Equal(5000, result.TotalBytes);
    }

    [Fact]
    public async Task Probe_NoLength()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(5000, 3),
            new ServeOptions(SupportsRanges: false, OmitContentLength: true));
        using var http = StrelaHttp.Create();

        var result = await Prober.ProbeAsync(http, new DownloadRequest(url), CancellationToken.None);

        Assert.Null(result.TotalBytes);
        Assert.False(result.SupportsRanges);
    }

    [Fact]
    public async Task Probe_ContentDisposition()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(100, 4),
            new ServeOptions(ContentDisposition: "attachment; filename=\"real.iso\""));
        using var http = StrelaHttp.Create();

        var result = await Prober.ProbeAsync(http, new DownloadRequest(url), CancellationToken.None);

        Assert.Equal("real.iso", result.FileName);
    }

    [Fact]
    public async Task Probe_ExplicitFileName_WinsAndIsSanitized()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(100, 5),
            new ServeOptions(ContentDisposition: "attachment; filename=\"real.iso\""));
        using var http = StrelaHttp.Create();

        var result = await Prober.ProbeAsync(http, new DownloadRequest(url, FileName: "my:file.zip"), CancellationToken.None);

        Assert.Equal(FileNameResolver.Sanitize("my:file.zip"), result.FileName);
        Assert.DoesNotContain(':', result.FileName);
    }

    [Fact]
    public async Task Probe_404_ReturnsStatus()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(100, 6), new ServeOptions(FixedStatus: 404));
        using var http = StrelaHttp.Create();

        var result = await Prober.ProbeAsync(http, new DownloadRequest(url), CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Null(result.TotalBytes);
        Assert.False(result.SupportsRanges);
        Assert.Equal("a.bin", result.FileName);
    }

    [Fact]
    public async Task Http_UsesHttp11AndBrowserUa()
    {
        await using var server = await TestFileServer.StartAsync();
        var url = server.AddFile("a.bin", TestData.Random(100, 7));
        using var http = StrelaHttp.Create();

        await Prober.ProbeAsync(http, new DownloadRequest(url), CancellationToken.None);

        Assert.Equal("HTTP/1.1", server.LastProtocol);
        Assert.Equal(StrelaHttp.UserAgent, server.LastUserAgent);
    }

    [Fact]
    public void NewRequest_SetsRangeHeadersCookiesAndReferer()
    {
        var req = new DownloadRequest(new Uri("http://example.test/x"), Referer: "http://ref.test/",
            Headers: new Dictionary<string, string> { ["X-Token"] = "abc" }, Cookies: "a=1; b=2");

        using var open = StrelaHttp.NewRequest(req, req.Url, from: 100);
        using var closed = StrelaHttp.NewRequest(req, req.Url, from: 100, toInclusive: 199);
        using var plain = StrelaHttp.NewRequest(req, req.Url);

        Assert.Equal(HttpMethod.Get, open.Method);
        Assert.Equal("bytes=100-", open.Headers.Range!.ToString());
        Assert.Equal("bytes=100-199", closed.Headers.Range!.ToString());
        Assert.Null(plain.Headers.Range);
        Assert.Equal("a=1; b=2", plain.Headers.GetValues("Cookie").Single());
        Assert.Equal("abc", plain.Headers.GetValues("X-Token").Single());
        Assert.Equal(new Uri("http://ref.test/"), plain.Headers.Referrer);
        Assert.Equal(StrelaHttp.UserAgent, plain.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Probe_RangeRejected_FallsBackToHead(HttpStatusCode getStatus)
    {
        var methods = new List<HttpMethod>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            methods.Add(request.Method);
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(getStatus);
            }

            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            head.Content.Headers.ContentLength = 777;
            head.Headers.AcceptRanges.Add("bytes");
            return head;
        }));

        var result = await Prober.ProbeAsync(http, new DownloadRequest(new Uri("http://example.test/a/b.zip")), CancellationToken.None);

        Assert.Equal([HttpMethod.Get, HttpMethod.Head], methods);
        Assert.Equal(777, result.TotalBytes);
        Assert.True(result.SupportsRanges);
        Assert.Equal("b.zip", result.FileName);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request; // как это делает настоящий обработчик
            return Task.FromResult(response);
        }
    }
}
