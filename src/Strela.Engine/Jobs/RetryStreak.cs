namespace Strela.Engine.Jobs;

/// <summary>
/// Consecutive failures of one connection (or of the probe): the attempt number that picks the backoff delay.
/// How long the whole job has been failing is tracked by the job itself, across all connections.
/// </summary>
internal sealed class RetryStreak
{
    public int Attempt { get; private set; }

    public void Reset() => Attempt = 0;

    /// <summary>Counts one more failure; returns its attempt number (1-based).</summary>
    public int Next() => ++Attempt;
}
