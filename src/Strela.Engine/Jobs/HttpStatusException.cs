using System.Net;

namespace Strela.Engine.Jobs;

/// <summary>An HTTP answer that is not what the request needs; classified by its status.</summary>
internal sealed class HttpStatusException(HttpStatusCode status, TimeSpan? retryAfter)
    : Exception($"HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static HttpStatusException From(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var retryAfter = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return new HttpStatusException(response.StatusCode, retryAfter);
    }
}
