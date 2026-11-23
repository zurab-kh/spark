using System.Net;
using System.Net.Sockets;

namespace Strela.Engine.Policies;

/// <summary>How the download job should react to a failed request.</summary>
public enum ErrorKind
{
    /// <summary>Network hiccup or server error: retry with backoff.</summary>
    Transient,
    /// <summary>Server asks us to slow down (429/503): retry and lower the connection count.</summary>
    Throttle,
    /// <summary>Retrying will not help: fail the download.</summary>
    Fatal,
}

public static class RetryPolicy
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Delay before retry number <paramref name="attempt"/> (1-based): 1, 2, 4, 8, 16, 30, 30... seconds.
    /// A server-supplied <paramref name="retryAfter"/> takes precedence, capped at 5 minutes.
    /// </summary>
    public static TimeSpan Delay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra)
            return ra < TimeSpan.Zero ? TimeSpan.Zero : ra > MaxRetryAfter ? MaxRetryAfter : ra;

        // 2^5 = 32 s already exceeds the cap; clamping the exponent also avoids shift overflow.
        var exponent = Math.Clamp(attempt - 1, 0, 5);
        var backoff = TimeSpan.FromSeconds(1 << exponent);
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }

    /// <summary>
    /// Classifies a failure. A failing HTTP status takes precedence; for a null or successful
    /// (1xx-3xx) status the exception decides.
    /// </summary>
    public static ErrorKind Classify(HttpStatusCode? status, Exception? ex)
    {
        if (status is { } s)
        {
            var code = (int)s;
            if (code is 429 or 503)
                return ErrorKind.Throttle;
            if (code >= 500)
                return ErrorKind.Transient;
            if (code >= 400) // includes 403, 404, 410
                return ErrorKind.Fatal;
        }

        return ex is IOException or HttpRequestException or TimeoutException or SocketException or TaskCanceledException
            ? ErrorKind.Transient
            : ErrorKind.Fatal;
    }
}
