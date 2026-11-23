using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Strela.TestServer;

/// <summary>
/// Встроенный HTTP-сервер, имитирующий плохие реальные серверы: лимит скорости на соединение,
/// обрывы, 503, отсутствие Range и т. п. «Соединение» здесь — один HTTP-запрос к файлу.
/// </summary>
public sealed class TestFileServer : IAsyncDisposable
{
    private const int ChunkSize = 16 * 1024;

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<int, FileEntry> _files = new();
    private int _nextId;

    public Uri BaseAddress { get; }

    private TestFileServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Слушает 127.0.0.1 на случайном свободном порту.</summary>
    public static Task<TestFileServer> StartAsync() => StartAsync(0);

    public static async Task<TestFileServer> StartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(1));
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();
        TestFileServer? server = null;
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();

        var address = app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));
        server = new TestFileServer(app, new Uri(address));
        return server;
    }

    /// <summary>Добавляет файл, доступный по /f/{n}/{name}.</summary>
    public Uri AddFile(string name, byte[] content, ServeOptions? options = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        _files[id] = new FileEntry(
            new FileVersion(content, ComputeETag(content), TruncateToSeconds(DateTimeOffset.UtcNow)),
            options ?? new ServeOptions());
        return new Uri(BaseAddress, $"/f/{id}/{Uri.EscapeDataString(name)}");
    }

    /// <summary>Подменяет содержимое: новый ETag и Last-Modified.</summary>
    public void ReplaceContent(Uri url, byte[] content)
    {
        var entry = Find(url);
        lock (entry.Gate)
        {
            // Last-Modified имеет точность до секунды — гарантируем, что он действительно изменится.
            var now = TruncateToSeconds(DateTimeOffset.UtcNow);
            var previous = entry.Version.LastModified;
            entry.Version = new FileVersion(content, ComputeETag(content), now > previous ? now : previous.AddSeconds(1));
        }
    }

    public void SetOptions(Uri url, ServeOptions options) => Find(url).Options = options;

    /// <summary>Сколько запросов к этому URL пришло (включая пробные и отклонённые).</summary>
    public int ConnectionsOpened(Uri url) => Volatile.Read(ref Find(url).Connections);

    /// <summary>Максимум одновременно обслуживаемых ответов для этого URL.</summary>
    public int MaxConcurrentSeen(Uri url) => Volatile.Read(ref Find(url).MaxInFlight);

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _app.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Долгие тестовые ответы не должны задерживать завершение теста.
        }

        await _app.DisposeAsync();
    }

    private FileEntry Find(Uri url) =>
        TryParseId(url.AbsolutePath, out var id) && _files.TryGetValue(id, out var entry)
            ? entry
            : throw new ArgumentException($"Unknown test file: {url}", nameof(url));

    private static bool TryParseId(string path, out int id)
    {
        id = 0;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0] == "f" &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var isHead = HttpMethods.IsHead(request.Method);

        if (!(isHead || HttpMethods.IsGet(request.Method)) ||
            !TryParseId(request.Path.Value ?? "", out var id) || !_files.TryGetValue(id, out var entry))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var connectionIndex = Interlocked.Increment(ref entry.Connections);
        var options = entry.Options;
        var version = entry.Version;

        if (options.FixedStatus is { } fixedStatus)
        {
            response.StatusCode = fixedStatus;
            return;
        }

        if (!entry.TryEnter(options.MaxConcurrent))
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            response.Headers.RetryAfter = "1";
            return;
        }

        try
        {
            await ServeAsync(context, version, options, connectionIndex, isHead);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Клиент оборвал соединение — для тестового сервера это штатная ситуация.
        }
        finally
        {
            entry.Leave();
        }
    }

    private static async Task ServeAsync(
        HttpContext context, FileVersion version, ServeOptions options, int connectionIndex, bool isHead)
    {
        var request = context.Request;
        var response = context.Response;
        var content = version.Content;
        long total = content.Length;

        long start = 0;
        long length = total;
        var partial = false;

        var rangeHeader = request.Headers.Range.ToString();
        if (rangeHeader.Length > 0 && ShouldHonorRange(options, rangeHeader))
        {
            if (!TryParseRange(rangeHeader, total, out start, out var end))
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{total}";
                return;
            }

            length = end - start + 1;
            partial = true;
        }

        response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        response.Headers.ETag = version.ETag;
        response.Headers.LastModified = version.LastModified.ToString("R", CultureInfo.InvariantCulture);
        response.ContentType = "application/octet-stream";
        if (options.SupportsRanges)
        {
            response.Headers.AcceptRanges = "bytes";
        }

        if (options.ContentDisposition is { } disposition)
        {
            response.Headers.ContentDisposition = disposition;
        }

        if (partial)
        {
            response.Headers.ContentRange = $"bytes {start}-{start + length - 1}/{total}";
        }

        if (!options.OmitContentLength)
        {
            response.ContentLength = length;
        }

        if (isHead)
        {
            return;
        }

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var rate = connectionIndex <= options.SlowConnectionCount
            ? options.SlowBytesPerSecond
            : options.BytesPerSecondPerConnection;
        var limit = options.DropAfterBytes is { } drop ? Math.Min(drop, length) : length;
        var aborted = context.RequestAborted;
        var clock = Stopwatch.StartNew();

        long sent = 0;
        while (sent < limit)
        {
            var count = (int)Math.Min(ChunkSize, limit - sent);
            await response.Body.WriteAsync(content.AsMemory((int)(start + sent), count), aborted);
            await response.Body.FlushAsync(aborted);
            sent += count;

            if (rate is { } bytesPerSecond && sent < limit)
            {
                // Токен-пейсинг по общему времени: сколько байт отдано — столько времени должно пройти.
                var due = TimeSpan.FromSeconds((double)sent / bytesPerSecond);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, aborted);
                }
            }
        }

        if (limit < length)
        {
            context.Abort();
        }
    }

    // IgnoreRangeAfterProbe: честно отвечаем только на пробу bytes=0-0, остальные Range игнорируем.
    private static bool ShouldHonorRange(ServeOptions options, string rangeHeader) =>
        options.SupportsRanges &&
        (!options.IgnoreRangeAfterProbe || rangeHeader.Replace(" ", "", StringComparison.Ordinal) == "bytes=0-0");

    /// <summary>Одиночный диапазон bytes=a-b или bytes=a-. Любое другое значение считаем неудовлетворимым.</summary>
    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = end = 0;
        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header[prefix.Length..].Trim();
        var dash = spec.IndexOf('-');
        if (dash <= 0 || spec.Contains(','))
        {
            return false;
        }

        if (!long.TryParse(spec[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            return false;
        }

        var endText = spec[(dash + 1)..];
        if (endText.Length == 0)
        {
            end = total - 1;
        }
        else if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end))
        {
            return false;
        }

        end = Math.Min(end, total - 1);
        return start < total && start <= end;
    }

    private static string ComputeETag(byte[] content) =>
        "\"" + Convert.ToHexString(SHA256.HashData(content), 0, 16).ToLowerInvariant() + "\"";

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private sealed record FileVersion(byte[] Content, string ETag, DateTimeOffset LastModified);

    private sealed class FileEntry(FileVersion version, ServeOptions options)
    {
        private int _inFlight;

        public readonly object Gate = new();
        public int Connections;
        public int MaxInFlight;

        public FileVersion Version { get => Volatile.Read(ref field); set => Volatile.Write(ref field, value); } = version;

        public ServeOptions Options { get => Volatile.Read(ref field); set => Volatile.Write(ref field, value); } = options;

        public bool TryEnter(int? maxConcurrent)
        {
            lock (Gate)
            {
                if (maxConcurrent is { } max && _inFlight >= max)
                {
                    return false;
                }

                _inFlight++;
                MaxInFlight = Math.Max(MaxInFlight, _inFlight);
                return true;
            }
        }

        public void Leave()
        {
            lock (Gate)
            {
                _inFlight--;
            }
        }
    }
}
