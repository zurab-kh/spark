using System.IO.Pipes;
using System.Text;

namespace Strela.Engine.Integration;

/// <summary>Принимает сообщения от других процессов по именованному каналу; Received вызывается на фоновом потоке.</summary>
public sealed class IpcServer : IAsyncDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly string _pipeName;
    private readonly IStrelaLog _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public IpcServer(string pipeName, IStrelaLog log)
    {
        _pipeName = pipeName;
        _log = log;
    }

    public event Action<IReadOnlyList<IpcMessage>>? Received;

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("IPC server already started.");
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? json;
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                json = await ReadAllAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn($"IPC: connection failed: {ex.Message}");
                await DelayQuietly(ct).ConfigureAwait(false);
                continue;
            }

            if (json is null) continue;
            try
            {
                Received?.Invoke(IpcProtocol.Parse(json));
            }
            catch (Exception ex)
            {
                _log.Warn($"IPC: handler failed: {ex.Message}");
            }
        }
    }

    private async Task<string?> ReadAllAsync(Stream pipe, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await pipe.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxMessageBytes)
            {
                _log.Warn($"IPC: message larger than {MaxMessageBytes} bytes ignored.");
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task DelayQuietly(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
