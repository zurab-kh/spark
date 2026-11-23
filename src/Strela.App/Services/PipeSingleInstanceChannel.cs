using Strela.Engine;
using Strela.Engine.Integration;

namespace Strela.App.Services;

/// <summary>Связь между экземплярами поверх IpcServer/IpcClient движка.</summary>
public sealed class PipeSingleInstanceChannel : ISingleInstanceChannel
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly IStrelaLog _log;
    private IpcServer? _server;

    public PipeSingleInstanceChannel(string? pipeName = null, TimeSpan? connectTimeout = null, IStrelaLog? log = null)
    {
        _pipeName = pipeName ?? IpcProtocol.PipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _log = log ?? NullLog.Instance;
    }

    public event Action<IReadOnlyList<string>>? Received;
    public event Action<IReadOnlyList<IpcMessage>>? MessagesReceived;

    public void Start()
    {
        if (_server is not null) return;
        _server = new IpcServer(_pipeName, _log);
        _server.Received += messages =>
        {
            Received?.Invoke(messages.Select(m => m.Url).ToList());
            MessagesReceived?.Invoke(messages);
        };
        _server.Start();
    }

    public Task<bool> SendAsync(IReadOnlyList<string> urls) =>
        IpcClient.SendAsync(_pipeName, urls.Select(u => new IpcMessage(u)).ToList(), _connectTimeout);

    public void Dispose()
    {
        if (_server is null) return;
        try { _server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch { /* выходим */ }
    }
}
