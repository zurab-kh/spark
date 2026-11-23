namespace Strela.Engine.Policies;

/// <summary>
/// Sliding-window download speed. The caller feeds (totalBytes, monotonic now) samples,
/// typically from a timer; the UI reads <see cref="BytesPerSecond"/>. Thread-safe.
/// </summary>
public sealed class SpeedMeter
{
    private readonly TimeSpan _window;
    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
    private readonly object _lock = new();
    private double _bytesPerSecond;

    public SpeedMeter(TimeSpan window) => _window = window;

    public void Sample(long totalBytes, TimeSpan now)
    {
        lock (_lock)
        {
            _samples.Enqueue((now, totalBytes));

            // Drop samples older than the window, but keep the newest one at or before
            // (now - window) as the baseline so the whole window is covered.
            var cutoff = now - _window;
            while (_samples.Count > 1)
            {
                // Peek the second-oldest: the oldest is expendable only if the next is still <= cutoff.
                var second = _samples.ElementAt(1);
                if (second.Time <= cutoff)
                    _samples.Dequeue();
                else
                    break;
            }

            var first = _samples.Peek();
            var span = now - first.Time;
            _bytesPerSecond = _samples.Count < 2 || span <= TimeSpan.Zero
                ? 0
                : (totalBytes - first.Bytes) / span.TotalSeconds;
        }
    }

    public double BytesPerSecond
    {
        get { lock (_lock) return _bytesPerSecond; }
    }
}
