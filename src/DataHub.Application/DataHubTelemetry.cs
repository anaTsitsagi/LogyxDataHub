using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DataHub.Application;

/// <summary>
/// DataHub's own traces and metrics. The hosts export them over OTLP (see DataHub.Hosting);
/// without an exporter they cost next to nothing.
/// </summary>
public static class DataHubTelemetry
{
    public const string Name = "DataHub";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    /// <summary>Processing time of a job, tagged with its outcome and error code.</summary>
    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "datahub.job.duration", unit: "s", description: "Time from claiming a job to its outcome.");

    /// <summary>Jobs finished, tagged with outcome (succeeded, failed, retry_scheduled) and error code.</summary>
    public static readonly Counter<long> JobsFinished = Meter.CreateCounter<long>(
        "datahub.jobs.finished", unit: "{job}", description: "Jobs that reached an outcome.");

    public static readonly Counter<long> JournalLinesImported = Meter.CreateCounter<long>(
        "datahub.import.journal_lines", unit: "{line}", description: "Journal lines written to staging datasets.");

    public static readonly Counter<long> UploadsCompleted = Meter.CreateCounter<long>(
        "datahub.uploads.completed", unit: "{upload}", description: "Uploads accepted and queued for processing.");

    public static readonly Counter<long> UploadedBytes = Meter.CreateCounter<long>(
        "datahub.uploads.bytes", unit: "By", description: "Size of accepted uploads.");
}
