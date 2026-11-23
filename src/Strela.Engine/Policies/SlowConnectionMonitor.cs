namespace Strela.Engine.Policies;

public readonly record struct WorkerSample(int WorkerId, double BytesPerSecond, long RemainingBytes);

/// <summary>
/// Finds connections that stay far slower than their peers so the job can replace them.
/// A worker is slow when its speed is below <c>threshold * median</c> and it still has at least
/// <c>minRemaining</c> bytes left; it is reported once that has held continuously for <c>grace</c>.
/// NOT thread-safe: call <see cref="Evaluate"/> from a single loop.
/// </summary>
public sealed class SlowConnectionMonitor
{
    private readonly double _threshold;
    private readonly TimeSpan _grace;
    private readonly long _minRemaining;
    private readonly Dictionary<int, TimeSpan> _slowSince = new();

    public SlowConnectionMonitor(double threshold, TimeSpan grace, long minRemaining)
    {
        _threshold = threshold;
        _grace = grace;
        _minRemaining = minRemaining;
    }

    /// <summary>Returns ids of workers that have been slow for at least the grace period. Reported ids are reset.</summary>
    public IReadOnlyList<int> Evaluate(IReadOnlyList<WorkerSample> samples, TimeSpan now)
    {
        if (samples.Count < 2)
            return [];

        var median = Median(samples);
        var flagged = new List<int>();

        foreach (var s in samples)
        {
            var slow = s.BytesPerSecond < _threshold * median && s.RemainingBytes >= _minRemaining;
            if (!slow)
            {
                _slowSince.Remove(s.WorkerId);
                continue;
            }

            if (!_slowSince.TryGetValue(s.WorkerId, out var since))
            {
                since = now;
                _slowSince[s.WorkerId] = since;
            }

            if (now - since >= _grace)
            {
                flagged.Add(s.WorkerId);
                _slowSince.Remove(s.WorkerId);
            }
        }

        // Forget workers that are no longer present.
        var present = samples.Select(s => s.WorkerId).ToHashSet();
        foreach (var id in _slowSince.Keys.Where(id => !present.Contains(id)).ToList())
            _slowSince.Remove(id);

        return flagged;
    }

    private static double Median(IReadOnlyList<WorkerSample> samples)
    {
        var speeds = samples.Select(s => s.BytesPerSecond).Order().ToArray();
        var mid = speeds.Length / 2;
        return speeds.Length % 2 == 1 ? speeds[mid] : (speeds[mid - 1] + speeds[mid]) / 2;
    }
}
