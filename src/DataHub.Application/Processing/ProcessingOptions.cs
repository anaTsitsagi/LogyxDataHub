namespace DataHub.Application.Processing;

public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    /// <summary>
    /// Scratch space for the downloaded ZIP and one extracted .tps file at a time. Needs roughly
    /// the upload size limit plus the largest table (size the Kubernetes ephemeral-storage limit accordingly).
    /// </summary>
    public string TempDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "datahub-worker");

    /// <summary>Attempts before a job with transient errors (database, S3 outages) is failed.</summary>
    public int MaxAttempts { get; set; } = 3;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A processing job whose heartbeat is older than this is considered abandoned by a dead worker.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A queued job not picked up within this time is published again (lost publish, or retry after a
    /// transient error). Keep it above the typical queue wait to avoid needless duplicate messages.
    /// </summary>
    public TimeSpan RequeueAfter { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromMinutes(15);
}

public enum JobOutcome
{
    Succeeded,
    Failed,

    /// <summary>Transient error; the job is back in Queued and the recovery sweep will publish it again.</summary>
    RetryScheduled,

    /// <summary>Duplicate or unknown message: the job is finished or being processed elsewhere.</summary>
    Skipped,
}
