using Strela.Engine.Segments;

namespace Strela.Engine.Tests;

public class SegmentPlannerTests
{
    private const long MB = 1L << 20;

    [Fact]
    public void InitialSplit_16Connections_32MB()
    {
        var segs = SegmentPlanner.InitialSplit(32 * MB, 16, MB);

        Assert.Equal(16, segs.Count);
        long expectedStart = 0;
        foreach (var s in segs)
        {
            Assert.Equal(expectedStart, s.Start);
            Assert.Equal(s.Start, s.Position);
            Assert.Equal(2 * MB, s.End - s.Start);
            expectedStart = s.End;
        }
        Assert.Equal(32 * MB, expectedStart);
    }

    [Fact]
    public void InitialSplit_SmallFile()
    {
        var segs = SegmentPlanner.InitialSplit(MB + MB / 2, 16, MB);

        var s = Assert.Single(segs);
        Assert.Equal(0, s.Start);
        Assert.Equal(MB + MB / 2, s.End);
    }

    [Fact]
    public void InitialSplit_LimitedByMinSegment()
    {
        var segs = SegmentPlanner.InitialSplit(4 * MB, 16, MB);

        Assert.Equal(4, segs.Count);
        Assert.All(segs, s => Assert.Equal(MB, s.End - s.Start));
        Assert.Equal(4 * MB, segs[^1].End);
    }

    [Fact]
    public void InitialSplit_LastSegmentTakesRemainder()
    {
        var segs = SegmentPlanner.InitialSplit(10 * MB + 7, 3, MB);

        Assert.Equal(3, segs.Count);
        Assert.Equal((10 * MB + 7) / 3, segs[0].End - segs[0].Start);
        Assert.Equal(0, segs[0].Start);
        Assert.Equal(segs[0].End, segs[1].Start);
        Assert.Equal(segs[1].End, segs[2].Start);
        Assert.Equal(10 * MB + 7, segs[2].End);
    }

    [Fact]
    public void InitialSplit_ZeroBytes_SingleEmptySegment()
    {
        var segs = SegmentPlanner.InitialSplit(0, 16, MB);

        var s = Assert.Single(segs);
        Assert.Equal(0, s.Start);
        Assert.Equal(0, s.End);
        Assert.Equal(0, s.Position);
        Assert.True(s.IsDone);
    }

    [Fact]
    public void Constructor_RejectsBrokenInvariant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Segment(-1, 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Segment(5, 10, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Segment(5, 10, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Segment(10, 5, 10));
    }

    [Fact]
    public void ReserveCommit_AdvancesAndStopsAtEnd()
    {
        var s = new Segment(100, 1100, 100);

        Assert.Equal((100L, 600), s.Reserve(600));
        Assert.Equal(700, s.Reserved);
        Assert.Equal(100, s.Position);

        Assert.Equal((700L, 400), s.Reserve(600));
        Assert.Equal(0, s.Reserve(600).Count);

        s.Commit(600);
        Assert.Equal(700, s.Position);
        Assert.Equal(400, s.Remaining);
        Assert.False(s.IsDone);

        s.Commit(400);
        Assert.True(s.IsDone);
        Assert.Equal(0, s.Remaining);
    }

    [Fact]
    public void Commit_BeyondReserved_Throws()
    {
        var s = new Segment(0, 1000, 0);
        s.Reserve(100);

        Assert.Throws<InvalidOperationException>(() => s.Commit(101));
        Assert.Throws<InvalidOperationException>(() => s.Commit(0));
    }

    [Fact]
    public void Abandon_ReturnsReservedToPosition()
    {
        var s = new Segment(0, 1000, 0);
        s.Reserve(300);
        s.Commit(100);

        s.Abandon();

        Assert.Equal(100, s.Position);
        Assert.Equal(100, s.Reserved);
        Assert.Equal((100L, 50), s.Reserve(50));
    }

    [Fact]
    public void SplitOff_HalvesFreeRemainder()
    {
        var s = new Segment(0, 8 * MB, 0);
        s.Reserve((int)MB);

        var tail = s.SplitOff(MB);

        Assert.NotNull(tail);
        Assert.Equal(4 * MB + MB / 2, tail!.Start);
        Assert.Equal(8 * MB, tail.End);
        Assert.Equal(tail.Start, tail.Position);
        Assert.Equal(tail.Start, tail.Reserved);
        Assert.Equal(4 * MB + MB / 2, s.End);
    }

    [Fact]
    public void SplitOff_AlignsMidDownTo4096()
    {
        var s = new Segment(0, 8 * MB + 1000, 0);

        var tail = s.SplitOff(MB);

        Assert.NotNull(tail);
        Assert.Equal(0, tail!.Start % 4096);
        Assert.Equal(s.End, tail.Start);
        Assert.Equal(4 * MB, tail.Start);
    }

    [Fact]
    public void SplitOff_RefusesBelowTwoMin()
    {
        long end = MB + (MB * 9) / 10; // 1.9 MB free
        var s = new Segment(0, end, 0);

        Assert.Null(s.SplitOff(MB));
        Assert.Equal(end, s.End);
    }

    [Fact]
    public void SplitOff_NeverBelowReserved()
    {
        var s = new Segment(0, 8 * MB, 0);
        s.Reserve((int)(7 * MB)); // 1 MB left free

        Assert.Null(s.SplitOff(MB));
        Assert.Equal(8 * MB, s.End);
    }

    [Fact]
    public void SplitOff_TinyFreeWithMinOne_UnalignedFallbackStaysAboveReserved()
    {
        var s = new Segment(0, 10, 0);
        s.Reserve(4);

        var tail = s.SplitOff(1);

        Assert.NotNull(tail);
        Assert.Equal(7, tail!.Start); // 4 + 6/2, alignment would give 0 (<= Reserved) so it is kept unaligned
        Assert.Equal(7, s.End);
        Assert.Equal(10, tail.End);
    }

    [Fact]
    public void SplitOff_NonPositiveMin_Throws()
    {
        var s = new Segment(0, 10, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SplitOff(0));
    }

    [Fact]
    public void Steal_PicksLargestFree()
    {
        var a = new Segment(0, 3 * MB, 0);
        var b = new Segment(3 * MB, 13 * MB, 3 * MB);
        var c = new Segment(13 * MB, 18 * MB, 13 * MB);

        var stolen = SegmentPlanner.Steal(new[] { a, b, c }, MB);

        Assert.NotNull(stolen);
        Assert.Equal(8 * MB, stolen!.Start);
        Assert.Equal(13 * MB, stolen.End);
        Assert.Equal(8 * MB, b.End);
        Assert.Equal(3 * MB, a.End);
        Assert.Equal(18 * MB, c.End);
    }

    [Fact]
    public void Steal_IgnoresDoneSegmentsAndReturnsNullWhenNothingToSplit()
    {
        var done = new Segment(0, 10 * MB, 10 * MB);
        var small = new Segment(10 * MB, 11 * MB, 10 * MB);

        Assert.Null(SegmentPlanner.Steal(new[] { done, small }, MB));
        Assert.Null(SegmentPlanner.Steal(Array.Empty<Segment>(), MB));
    }

    [Fact]
    public void Steal_TieGoesToFirstInList()
    {
        var a = new Segment(0, 4 * MB, 0);
        var b = new Segment(4 * MB, 8 * MB, 4 * MB);

        var stolen = SegmentPlanner.Steal(new[] { a, b }, MB);

        Assert.NotNull(stolen);
        Assert.Equal(2 * MB, a.End);
        Assert.Equal(8 * MB, b.End);
    }

    [Fact]
    public void Concurrent_ReserveAndSplit_CoverExactlyOnce()
    {
        const long total = 64 * MB;
        long totalSteals = 0;

        for (int iteration = 0; iteration < 25; iteration++)
            totalSteals += RunConcurrentIteration(total);

        // The stealers must actually have raced with the workers, otherwise the test proves nothing.
        Assert.True(totalSteals > 0, "no steal ever happened");
    }

    private static int RunConcurrentIteration(long total)
    {
        const int workers = 8, stealers = 2, chunk = 64 * 1024;

        var pool = SegmentPlanner.InitialSplit(total, 2, MB);
        var owned = new HashSet<Segment>();
        var poolLock = new object();
        var written = new List<(long Offset, int Count)>[workers];
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        int steals = 0;
        int stealsInFlight = 0; // guarded by poolLock: a cut-off tail that is not yet in the pool
        bool stop = false;
        using var start = new Barrier(workers + stealers);

        // A segment is owned by exactly one worker at a time, like in the real engine.
        Segment? Claim()
        {
            lock (poolLock)
            {
                foreach (var s in pool)
                    if (!s.IsDone && owned.Add(s))
                        return s;
                return null;
            }
        }

        // Finished only when nothing is left to download and no stolen tail is on its way into the pool.
        bool AllDone()
        {
            lock (poolLock) return stealsInFlight == 0 && pool.All(s => s.IsDone);
        }

        var threads = new List<Thread>();
        for (int w = 0; w < workers; w++)
        {
            int id = w;
            written[id] = new();
            threads.Add(new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    while (!AllDone())
                    {
                        var seg = Claim();
                        if (seg is null) { Thread.Yield(); continue; }
                        while (true)
                        {
                            var (offset, count) = seg.Reserve(chunk);
                            if (count == 0) break;
                            // simulated "write to disk" window, during which stealers may split
                            Thread.SpinWait(50);
                            written[id].Add((offset, count));
                            seg.Commit(count);
                        }
                        lock (poolLock) owned.Remove(seg);
                    }
                }
                catch (Exception e) { errors.Enqueue(e); }
            }));
        }
        for (int t = 0; t < stealers; t++)
        {
            threads.Add(new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    while (!Volatile.Read(ref stop))
                    {
                        Segment[] snapshot;
                        lock (poolLock)
                        {
                            snapshot = pool.ToArray();
                            stealsInFlight++;
                        }
                        var stolen = SegmentPlanner.Steal(snapshot, MB);
                        lock (poolLock)
                        {
                            if (stolen is not null) pool.Add(stolen);
                            stealsInFlight--;
                        }
                        if (stolen is not null) Interlocked.Increment(ref steals);
                        else Thread.Yield();
                    }
                }
                catch (Exception e) { errors.Enqueue(e); }
            }));
        }

        foreach (var t in threads) t.Start();
        for (int i = 0; i < workers; i++) threads[i].Join();
        Volatile.Write(ref stop, true);
        for (int i = workers; i < threads.Count; i++) threads[i].Join();

        Assert.Empty(errors);

        // 1. every committed write range, over all workers, tiles [0, total) exactly once
        var ranges = written.SelectMany(l => l).OrderBy(r => r.Offset).ToList();
        long cursor = 0;
        foreach (var (offset, count) in ranges)
        {
            Assert.Equal(cursor, offset);
            cursor += count;
        }
        Assert.Equal(total, cursor);

        // 2. the final union of [Start, Position) tiles [0, total) with no overlap and no hole
        var final = pool.OrderBy(s => s.Start).ToList();
        cursor = 0;
        foreach (var s in final)
        {
            Assert.Equal(cursor, s.Start);
            Assert.Equal(s.End, s.Position);
            Assert.Equal(s.End, s.Reserved);
            cursor = s.Position;
        }
        Assert.Equal(total, cursor);

        // 3. every write landed inside the final [Start, End) of one segment (nobody wrote past a cut)
        foreach (var (offset, count) in ranges)
        {
            var owner = final.Single(s => offset >= s.Start && offset < s.End);
            Assert.True(offset + count <= owner.End, $"write [{offset},{offset + count}) crosses End {owner.End}");
        }

        return steals;
    }
}
