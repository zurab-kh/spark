using System.Net;

namespace Strela.Engine.Probe;

public static class Prober
{
    /// <summary>
    /// Разведка: GET Range bytes=0-0 (при 416/405 — HEAD). Тело не читается. Статус ≥ 400 возвращается
    /// в <see cref="ProbeResult.Status"/>, сетевые исключения — вызывающему.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(HttpClient http, DownloadRequest req, CancellationToken ct)
    {
        using var response = await SendAsync(http, req, HttpMethod.Get, rangeProbe: true, ct);

        if (response.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.MethodNotAllowed)
        {
            using var head = await SendAsync(http, req, HttpMethod.Head, rangeProbe: false, ct);
            if ((int)head.StatusCode >= 400)
            {
                return Build(req, head, null, false);
            }

            return Build(req, head, head.Content.Headers.ContentLength,
                head.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase));
        }

        return response.StatusCode switch
        {
            HttpStatusCode.PartialContent => Build(req, response, response.Content.Headers.ContentRange?.Length, true),
            HttpStatusCode.OK => Build(req, response, response.Content.Headers.ContentLength, false),
            _ => Build(req, response, null, false),
        };
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient http, DownloadRequest req, HttpMethod method, bool rangeProbe, CancellationToken ct)
    {
        var request = Http.StrelaHttp.NewRequest(req, req.Url, rangeProbe ? 0 : null, rangeProbe ? 0 : null);
        request.Method = method;
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static ProbeResult Build(DownloadRequest req, HttpResponseMessage response, long? total, bool supportsRanges)
    {
        var finalUri = response.RequestMessage!.RequestUri!;
        var fileName = string.IsNullOrWhiteSpace(req.FileName)
            ? FileNameResolver.Resolve(response.Content.Headers.ContentDisposition, finalUri)
            : FileNameResolver.Sanitize(req.FileName);

        return new ProbeResult(
            finalUri,
            total,
            supportsRanges,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            fileName,
            response.StatusCode);
    }
}
