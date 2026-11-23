using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Strela.App.Services;

/// <summary>
/// Именованный канал, формат совместим с IpcProtocol: JSON-массив объектов {"url": "..."}.
/// Task 12: заменить на тонкий адаптер к IpcServer/IpcClient.
/// </summary>
public sealed class PipeSingleInstanceChannel : ISingleInstanceChannel
{
    private const int MaxMessageBytes = 1 << 20;

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public PipeSingleInstanceChannel(string? pipeName = null, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName ?? "Strela.Ipc.v1." + Environment.UserName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public event Action<IReadOnlyList<string>>? Received;

    public void Start() => _loop ??= Task.Run(ServeAsync);

    public async Task<bool> SendAsync(IReadOnlyList<string> urls)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(_connectTimeout);
            await client.ConnectAsync(cts.Token);
            var json = JsonSerializer.Serialize(urls.Select(u => new Dictionary<string, string> { ["url"] = u }));
            await client.WriteAsync(Encoding.UTF8.GetBytes(json), cts.Token);
            await client.FlushAsync(cts.Token);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                var urls = await ReadUrlsAsync(server, ct);
                Received?.Invoke(urls);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Порванное соединение или мусор — слушаем дальше.
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadUrlsAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxMessageBytes) return Array.Empty<string>();
        }
        try
        {
            using var doc = JsonDocument.Parse(ms.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var urls = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    && u.GetString() is { Length: > 0 } str)
                    urls.Add(str);
            }
            return urls;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public void Dispose() => _cts.Cancel();
}
