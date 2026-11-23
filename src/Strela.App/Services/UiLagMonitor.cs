using System.Diagnostics;
using System.Windows.Threading;
using Strela.Engine;

namespace Strela.App.Services;

/// <summary>Каждые 50 мс просит UI-поток отметиться и меряет задержку; больше 100 мс — в лог.</summary>
public sealed class UiLagMonitor : IDisposable
{
    private const int IntervalMs = 50;
    private const long WarnMs = 100;

    private readonly Dispatcher _dispatcher;
    private readonly IStrelaLog _log;
    private readonly Timer _timer;
    private int _pending;

    public UiLagMonitor(Dispatcher d, IStrelaLog log)
    {
        _dispatcher = d;
        _log = log;
        _timer = new Timer(_ => Probe(), null, IntervalMs, IntervalMs);
    }

    private void Probe()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;   // предыдущая проба ещё не дошла до UI
        var start = Stopwatch.GetTimestamp();
        try
        {
            _dispatcher.InvokeAsync(() =>
            {
                var ms = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Volatile.Write(ref _pending, 0);
                if (ms > WarnMs) _log.Warn($"UI lag {ms} ms");
            }, DispatcherPriority.Normal);
        }
        catch
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
