namespace Strela.Engine.Segments;

/// <summary>
/// Live piece of a download: absolute byte range [Start, End). Invariant at all times:
/// Start &lt;= Position &lt;= Reserved &lt;= End. One worker owns a segment at a time and does
/// Reserve → write to disk → Commit; any other thread may only call <see cref="SplitOff"/>,
/// which cuts strictly beyond Reserved, so a worker never writes past End.
/// All state is guarded by a single lock.
/// </summary>
public sealed class Segment
{
    /// <summary>Granularity the cut point is aligned down to.</summary>
    private const long Alignment = 4096;

    private readonly object _lock = new();
    private long _end;
    private long _position;
    private long _reserved;
    private double _bytesPerSecond;

    /// <param name="end">Exclusive.</param>
    public Segment(long start, long end, long position)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start), start, "Start must be non-negative.");
        if (end < start)
            throw new ArgumentOutOfRangeException(nameof(end), end, "End must not be below Start.");
        if (position < start || position > end)
            throw new ArgumentOutOfRangeException(nameof(position), position, "Position must lie within [Start, End].");

        Start = start;
        _end = end;
        _position = position;
        _reserved = position;
    }

    public long Start { get; }

    public long End { get { lock (_lock) return _end; } }

    /// <summary>Confirmed written up to (absolute).</summary>
    public long Position { get { lock (_lock) return _position; } }

    /// <summary>Handed out to the worker up to (absolute); Position &lt;= Reserved &lt;= End.</summary>
    public long Reserved { get { lock (_lock) return _reserved; } }

    public long Remaining { get { lock (_lock) return _end - _position; } }

    public bool IsDone { get { lock (_lock) return _position >= _end; } }

    /// <summary>(Start, End, Position) read under one lock acquisition, so the three values belong together.</summary>
    public (long Start, long End, long Position) Snapshot()
    {
        lock (_lock) return (Start, _end, _position);
    }

    /// <summary>Speed of the connection that owns the segment, as last measured; 0 = not measured yet.</summary>
    public double BytesPerSecond
    {
        get => Volatile.Read(ref _bytesPerSecond);
        set => Volatile.Write(ref _bytesPerSecond, value);
    }

    /// <summary>Bytes not yet reserved, i.e. the part that can still be split off.</summary>
    internal long Free { get { lock (_lock) return _end - _reserved; } }

    /// <summary>Reserves up to <paramref name="available"/> bytes at the current reserve point. Count 0 means the end was reached.</summary>
    public (long Offset, int Count) Reserve(int available)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(available);
        lock (_lock)
        {
            long offset = _reserved;
            int count = (int)Math.Min(available, _end - _reserved);
            _reserved += count;
            return (offset, count);
        }
    }

    /// <summary>Confirms <paramref name="n"/> reserved bytes as written to disk. Commits arrive in order.</summary>
    public void Commit(int n)
    {
        lock (_lock)
        {
            if (n <= 0 || n > _reserved - _position)
                throw new InvalidOperationException(
                    $"Commit of {n} bytes is outside (0, reserved-but-uncommitted = {_reserved - _position}].");
            _position += n;
        }
    }

    /// <summary>The connection broke: bytes reserved but not committed will be downloaded again.</summary>
    public void Abandon()
    {
        lock (_lock) _reserved = _position;
    }

    /// <summary>
    /// Cuts the second half of the not-yet-reserved remainder into a new segment [mid, End) and
    /// shrinks this one to [Start, mid). Returns null when the free remainder is under 2 * <paramref name="minRemaining"/>.
    /// </summary>
    public Segment? SplitOff(long minRemaining)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minRemaining);
        lock (_lock)
        {
            long free = _end - _reserved;
            if (free < 2 * minRemaining)
                return null;

            long mid = _reserved + free / 2;
            long aligned = mid - mid % Alignment;
            if (aligned > _reserved)
                mid = aligned;

            var tail = new Segment(mid, _end, mid);
            _end = mid;
            return tail;
        }
    }
}
