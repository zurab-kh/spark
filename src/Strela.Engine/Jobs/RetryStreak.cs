namespace Strela.Engine.Jobs;

/// <summary>A run of consecutive failures of one connection (or of the probe): attempt number and since when.</summary>
internal sealed class RetryStreak
{
    private long _sinceMs = -1;

    public int Attempt { get; private set; }

    public void Reset()
    {
        Attempt = 0;
        _sinceMs = -1;
    }

    /// <summary>Counts one more failure; returns how long the streak has lasted.</summary>
    public TimeSpan Register()
    {
        var now = Environment.TickCount64;
        if (_sinceMs < 0)
            _sinceMs = now;
        Attempt++;
        return TimeSpan.FromMilliseconds(now - _sinceMs);
    }
}
