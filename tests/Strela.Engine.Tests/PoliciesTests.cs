using System.Net;
using System.Net.Sockets;
using Strela.Engine.Policies;

namespace Strela.Engine.Tests;

public class PoliciesTests
{
    private const long MB = 1024 * 1024;
    private const long KB = 1024;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(6, 30)]
    [InlineData(20, 30)]
    [InlineData(1000, 30)]
    public void Delay_Backoff(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.Delay(attempt, null));
    }

    [Fact]
    public void Delay_RetryAfterWins_Capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.Delay(1, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromMinutes(5), RetryPolicy.Delay(1, TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.Delay(3, TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ErrorKind.Fatal)]
    [InlineData(HttpStatusCode.Gone, ErrorKind.Fatal)]
    [InlineData(HttpStatusCode.Forbidden, ErrorKind.Fatal)]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.Fatal)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorKind.Fatal)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable, ErrorKind.Fatal)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorKind.Throttle)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorKind.Throttle)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, ErrorKind.Transient)]
    public void Classify_Maps(HttpStatusCode status, ErrorKind expected)
    {
        Assert.Equal(expected, RetryPolicy.Classify(status, null));
    }

    [Fact]
    public void Classify_ByException_WhenNoStatus()
    {
        Assert.Equal(ErrorKind.Transient, RetryPolicy.Classify(null, new IOException()));
        Assert.Equal(ErrorKind.Transient, RetryPolicy.Classify(null, new HttpRequestException()));
        Assert.Equal(ErrorKind.Transient, RetryPolicy.Classify(null, new TimeoutException()));
        Assert.Equal(ErrorKind.Transient, RetryPolicy.Classify(null, new SocketException()));
        Assert.Equal(ErrorKind.Transient, RetryPolicy.Classify(null, new TaskCanceledException()));
        Assert.Equal(ErrorKind.Fatal, RetryPolicy.Classify(null, new InvalidOperationException()));
        Assert.Equal(ErrorKind.Fatal, RetryPolicy.Classify(null, null));
    }

    [Fact]
    public void Classify_StatusWinsOverException_ButSuccessFallsBackToException()
    {
        Assert.Equal(ErrorKind.Fatal, RetryPolicy.Classify(HttpStatusCode.NotFound, new IOException()));
        Assert.Equal(ErrorKind.Transient, RetryPolicy.Classify(HttpStatusCode.OK, new IOException()));
        Assert.Equal(ErrorKind.Fatal, RetryPolicy.Classify(HttpStatusCode.PartialContent, new InvalidOperationException()));
    }

    [Fact]
    public void SpeedMeter_SlidingWindow()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(3));
        Assert.Equal(0, meter.BytesPerSecond);

        meter.Sample(0, TimeSpan.Zero);
        Assert.Equal(0, meter.BytesPerSecond);

        meter.Sample(3 * MB, TimeSpan.FromSeconds(3));
        Assert.Equal(MB, meter.BytesPerSecond, 1);

        meter.Sample(3 * MB, TimeSpan.FromSeconds(7));
        Assert.Equal(0, meter.BytesPerSecond);
    }

    [Fact]
    public void SpeedMeter_ZeroSpan_IsZero()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(3));
        meter.Sample(0, TimeSpan.FromSeconds(1));
        meter.Sample(MB, TimeSpan.FromSeconds(1));

        Assert.Equal(0, meter.BytesPerSecond);
    }

    private static IReadOnlyList<WorkerSample> Workers(long slowSpeed, long slowRemaining = 50 * MB) =>
    [
        new(1, MB, 50 * MB),
        new(2, slowSpeed, slowRemaining),
        new(3, MB, 50 * MB),
        new(4, MB, 50 * MB),
    ];

    private static SlowConnectionMonitor NewMonitor() =>
        new(threshold: 0.25, grace: TimeSpan.FromSeconds(6), minRemaining: 2 * MB);

    [Fact]
    public void Slow_FlaggedAfterGrace()
    {
        var monitor = NewMonitor();
        var samples = Workers(100 * KB);

        for (var t = 0.0; t <= 5.0; t += 0.5)
            Assert.Empty(monitor.Evaluate(samples, TimeSpan.FromSeconds(t)));

        Assert.Equal([2], monitor.Evaluate(samples, TimeSpan.FromSeconds(6.1)));
    }

    [Fact]
    public void Slow_FlaggedExactlyAtGrace_AndTimerResetsAfterReturn()
    {
        var monitor = NewMonitor();
        var samples = Workers(100 * KB);

        Assert.Empty(monitor.Evaluate(samples, TimeSpan.Zero));
        Assert.Equal([2], monitor.Evaluate(samples, TimeSpan.FromSeconds(6)));
        // Reset after being returned: needs a fresh grace period.
        Assert.Empty(monitor.Evaluate(samples, TimeSpan.FromSeconds(6.5)));
        Assert.Empty(monitor.Evaluate(samples, TimeSpan.FromSeconds(12)));
        Assert.Equal([2], monitor.Evaluate(samples, TimeSpan.FromSeconds(12.5)));
    }

    [Fact]
    public void Slow_NotFlaggedWhenLittleLeft()
    {
        var monitor = NewMonitor();
        var samples = Workers(100 * KB, slowRemaining: 1 * MB);

        Assert.Empty(monitor.Evaluate(samples, TimeSpan.Zero));
        Assert.Empty(monitor.Evaluate(samples, TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void Slow_RecoveryResetsTimer()
    {
        var monitor = NewMonitor();
        var slow = Workers(100 * KB);
        var normal = Workers(MB);

        Assert.Empty(monitor.Evaluate(slow, TimeSpan.Zero));
        Assert.Empty(monitor.Evaluate(slow, TimeSpan.FromSeconds(4)));
        Assert.Empty(monitor.Evaluate(normal, TimeSpan.FromSeconds(5)));
        Assert.Empty(monitor.Evaluate(slow, TimeSpan.FromSeconds(6)));
        Assert.Empty(monitor.Evaluate(slow, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Slow_NeedsAtLeastTwoSamples()
    {
        var monitor = NewMonitor();
        IReadOnlyList<WorkerSample> one = [new(1, 1, 50 * MB)];

        Assert.Empty(monitor.Evaluate(one, TimeSpan.Zero));
        Assert.Empty(monitor.Evaluate(one, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Slow_SparseSamples_ResetTracking()
    {
        var monitor = NewMonitor();
        var slow = Workers(100 * KB);
        IReadOnlyList<WorkerSample> single = [new(2, 100 * KB, 50 * MB)];

        Assert.Empty(monitor.Evaluate(slow, TimeSpan.Zero));
        Assert.Empty(monitor.Evaluate(single, TimeSpan.FromSeconds(3)));
        // Not observed continuously: the timer starts over at t=7.
        Assert.Empty(monitor.Evaluate(slow, TimeSpan.FromSeconds(7)));
        Assert.Empty(monitor.Evaluate(slow, TimeSpan.FromSeconds(12)));
        Assert.Equal([2], monitor.Evaluate(slow, TimeSpan.FromSeconds(13)));
    }

    [Fact]
    public void Slow_ForgetsWorkersThatDisappear()
    {
        var monitor = NewMonitor();
        var slow = Workers(100 * KB);
        IReadOnlyList<WorkerSample> withoutTwo = [new(1, MB, 50 * MB), new(3, MB, 50 * MB)];

        Assert.Empty(monitor.Evaluate(slow, TimeSpan.Zero));
        Assert.Empty(monitor.Evaluate(withoutTwo, TimeSpan.FromSeconds(3)));
        // Worker 2 returns: its slow timer starts over at t=4, not t=0.
        Assert.Empty(monitor.Evaluate(slow, TimeSpan.FromSeconds(4)));
        Assert.Empty(monitor.Evaluate(slow, TimeSpan.FromSeconds(7)));
        Assert.Equal([2], monitor.Evaluate(slow, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Slow_MedianOfEvenCount_AveragesMiddle()
    {
        // speeds 100, 200, 800, 1000 -> median 500; threshold 0.25 -> 125: only worker 1 is slow.
        var monitor = new SlowConnectionMonitor(0.25, TimeSpan.Zero, 0);
        IReadOnlyList<WorkerSample> samples =
        [
            new(1, 100, MB), new(2, 200, MB), new(3, 800, MB), new(4, 1000, MB),
        ];

        Assert.Equal([1], monitor.Evaluate(samples, TimeSpan.Zero));
    }

    [Fact]
    public void HostLimits_LowerThenCeiling()
    {
        var limits = new HostLimits();

        Assert.Equal(16, limits.Ceiling("h", 16));
        limits.Lower("h", 4);

        Assert.Equal(4, limits.Ceiling("h", 16));
        Assert.Equal(2, limits.Ceiling("h", 2));
        Assert.Equal(16, limits.Ceiling("other", 16));
    }

    [Fact]
    public void HostLimits_CaseInsensitive_NeverRaises_MinOne()
    {
        var limits = new HostLimits();

        limits.Lower("Example.COM", 4);
        limits.Lower("example.com", 8);
        Assert.Equal(4, limits.Ceiling("EXAMPLE.com", 16));

        limits.Lower("example.com", 0);
        Assert.Equal(1, limits.Ceiling("example.com", 16));
    }
}
