using System.IO.Pipes;
using System.Text;

namespace Strela.Engine.Integration;

public static class IpcClient
{
    /// <summary>Отправляет сообщения работающему экземпляру. false, если сервер не ответил за timeout или канал оборвался.</summary>
    public static async Task<bool> SendAsync(string pipeName, IReadOnlyList<IpcMessage> messages, TimeSpan timeout)
    {
        var payload = Encoding.UTF8.GetBytes(IpcProtocol.Serialize(messages));
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)).ConfigureAwait(false);
            await client.WriteAsync(payload).ConfigureAwait(false);
            await client.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }
}
