using Strela.Engine.Manager;

namespace Strela.Engine.Jobs;

/// <summary>How a run of a download ended: Completed, Paused (cancelled or no disk space) or Error.</summary>
public sealed record JobOutcome(DownloadStatus Status, string? Error = null, string? FinalPath = null);

/// <summary>Ends the whole job with <see cref="Outcome"/> (a fatal server answer, a local write failure…).</summary>
internal sealed class JobFailedException(JobOutcome outcome, Exception? inner = null)
    : Exception(outcome.Error, inner)
{
    public JobOutcome Outcome { get; } = outcome;
}
