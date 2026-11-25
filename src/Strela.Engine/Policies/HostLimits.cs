using System.Collections.Concurrent;

namespace Strela.Engine.Policies;

/// <summary>
/// Per-host connection ceilings learned at runtime (e.g. after 429/503 answers). A ceiling only ever goes down while
/// it lasts and is forgotten <see cref="DefaultForgetAfter"/> after it was last lowered: a server that was busy or
/// annoyed once does not cap every later download from it for the rest of the session. Thread-safe.
/// </summary>
public sealed class HostLimits
{
    public static readonly TimeSpan DefaultForgetAfter = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (int Ceiling, long Until)> _ceilings = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _forgetMs;

    public HostLimits(TimeSpan? forgetAfter = null) =>
        _forgetMs = (long)(forgetAfter ?? DefaultForgetAfter).TotalMilliseconds;

    /// <summary>The number of connections allowed to <paramref name="host"/>: the request, capped by any learned ceiling.</summary>
    public int Ceiling(string host, int requested)
    {
        if (!_ceilings.TryGetValue(host, out var entry))
            return requested;
        if (Environment.TickCount64 >= entry.Until)
        {
            _ceilings.TryRemove(new KeyValuePair<string, (int, long)>(host, entry));
            return requested;
        }
        return Math.Min(requested, entry.Ceiling);
    }

    /// <summary>Records that only <paramref name="workingConnections"/> worked for <paramref name="host"/> (at least 1); never raises a live ceiling.</summary>
    public void Lower(string host, int workingConnections)
    {
        var value = Math.Max(1, workingConnections);
        var now = Environment.TickCount64;
        var until = now + _forgetMs;
        // Only an actual lowering restarts the clock: a stray 429 at a higher count must not keep an old ceiling alive.
        _ceilings.AddOrUpdate(host, (value, until),
            (_, existing) => now >= existing.Until || value < existing.Ceiling ? (value, until) : existing);
    }
}
