namespace Strela.Engine.Segments;

public static class SegmentPlanner
{
    /// <summary>
    /// n = clamp(totalBytes / minSegmentBytes, 1, connections) contiguous segments of equal size,
    /// the last one takes the remainder. A non-positive total gives a single empty segment [0, 0).
    /// </summary>
    public static List<Segment> InitialSplit(long totalBytes, int connections, long minSegmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSegmentBytes);

        if (totalBytes <= 0)
            return [new Segment(0, 0, 0)];

        int n = (int)Math.Clamp(totalBytes / minSegmentBytes, 1, connections);
        long part = totalBytes / n;
        var result = new List<Segment>(n);
        for (int i = 0; i < n; i++)
        {
            long start = i * part;
            long end = i == n - 1 ? totalBytes : start + part;
            result.Add(new Segment(start, end, start));
        }
        return result;
    }

    /// <summary>
    /// Picks the unfinished segment with the most unreserved bytes (ties: first in the list) and cuts its tail off.
    /// With <paramref name="minOwnerSeconds"/> a segment whose owner (at its measured <see cref="Segment.BytesPerSecond"/>)
    /// would finish the free part sooner is left alone: splitting it saves less than the new request costs, and a
    /// burst of tiny requests at the end of a download is what makes servers answer 429.
    /// The candidate is chosen without holding its lock, so it may change meanwhile — SplitOff re-checks under the lock.
    /// </summary>
    public static Segment? Steal(IReadOnlyList<Segment> segments, long minSegmentBytes, double minOwnerSeconds = 0)
    {
        Segment? best = null;
        long bestFree = -1;
        foreach (var s in segments)
        {
            if (s.IsDone) continue;
            long free = s.Free;
            var speed = s.BytesPerSecond;
            if (speed > 0 && free < speed * minOwnerSeconds)
                continue;
            if (free > bestFree)
            {
                best = s;
                bestFree = free;
            }
        }
        return best?.SplitOff(minSegmentBytes);
    }
}
