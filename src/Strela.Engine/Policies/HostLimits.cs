using System.Collections.Concurrent;

namespace Strela.Engine.Policies;

/// <summary>
/// Per-host connection ceilings learned at runtime (e.g. after 429/503 answers).
/// Lives as long as the object; ceilings only ever go down. Thread-safe.
/// </summary>
public sealed class HostLimits
{
    private readonly ConcurrentDictionary<string, int> _ceilings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of connections allowed to <paramref name="host"/>: the request, capped by any learned ceiling.</summary>
    public int Ceiling(string host, int requested) =>
        _ceilings.TryGetValue(host, out var ceiling) ? Math.Min(requested, ceiling) : requested;

    /// <summary>Records that only <paramref name="workingConnections"/> worked for <paramref name="host"/> (at least 1); never raises an existing ceiling.</summary>
    public void Lower(string host, int workingConnections)
    {
        var value = Math.Max(1, workingConnections);
        _ceilings.AddOrUpdate(host, value, (_, existing) => Math.Min(existing, value));
    }
}
