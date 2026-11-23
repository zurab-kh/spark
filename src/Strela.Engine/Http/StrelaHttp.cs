using System.Net;
using System.Net.Http.Headers;

namespace Strela.Engine.Http;

public static class StrelaHttp
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>Новый клиент: только HTTP/1.1, без распаковки, без общего таймаута (его ставят вызывающие).</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 256,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };

        return new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>GET с заголовками запроса; Range ставится только если задан <paramref name="from"/>.</summary>
    public static HttpRequestMessage NewRequest(DownloadRequest req, Uri url, long? from = null, long? toInclusive = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        Apply(request, req);

        if (from is { } start)
        {
            request.Headers.Range = new RangeHeaderValue(start, toInclusive);
        }

        return request;
    }

    /// <summary>Заголовки запроса из <paramref name="req"/> (UA, Referer, пользовательские, Cookie) — без Range.</summary>
    internal static void Apply(HttpRequestMessage request, DownloadRequest req)
    {
        // Пользовательский User-Agent заменяет встроенный, а не дописывается к нему.
        var customUserAgent = req.Headers?.Keys.Any(k => string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase)) == true;
        if (!customUserAgent)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(req.Referer) && Uri.TryCreate(req.Referer, UriKind.Absolute, out var referer))
        {
            request.Headers.Referrer = referer;
        }

        if (req.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(req.Cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", req.Cookies);
        }
    }
}
