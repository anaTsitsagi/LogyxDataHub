using System.Security.Cryptography;
using DataHub.Application.Datasets;
using DataHub.Domain;
using DataHub.Oris;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Processing;

/// <summary>
/// Processes one queued upload: download from S3 → verify → import into a staging dataset → activate.
/// The company's current data is replaced only when every step succeeds.
/// </summary>
/// <remarks>
/// Safe to call more than once for the same job (duplicate or redelivered messages): the job is
/// claimed with a conditional update, so only one worker processes it at a time.
/// </remarks>
public sealed class JobProcessor(
    IDataHubDb db,
    IDatasetStore datasets,
    IFileStore files,
    IEnumerable<IUploadProcessor> processors,
    ProcessingNotifier notifier,
    IServiceScopeFactory scopes,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<JobProcessor> logger)
{
    public async Task<JobOutcome> ProcessAsync(ProcessingJobMessage message, CancellationToken ct)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = message.JobId,
            ["TenantId"] = message.TenantId,
            ["CorrelationId"] = message.CorrelationId,
        });

        if (!await TryClaimAsync(message.JobId, ct))
        {
            logger.LogInformation("Job {JobId} skipped: already finished or being processed", message.JobId);
            return JobOutcome.Skipped;
        }

        // The database, not the message, is the source of truth for what to process.
        var job = await db.ProcessingJobs.SingleAsync(j => j.Id == message.JobId, ct);
        var upload = await db.Uploads.SingleAsync(u => u.Id == job.UploadId, ct);
        logger.LogInformation("Job {JobId} started (attempt {Attempt}): upload {UploadId}, {SizeBytes} bytes",
            job.Id, job.Attempts, upload.Id, upload.SizeBytes);

        await datasets.AbandonStagingAsync(job.Id, ct);
        Guid? datasetId = null;
        await using var heartbeat = new Heartbeat(job.Id, scopes, options.Value.HeartbeatInterval, clock, logger);
        try
        {
            var processor = processors.FirstOrDefault(p => p.Type == upload.Type)
                ?? throw new DataHubException(ErrorKind.Validation, ProcessingErrors.TypeNotSupported, $"No processor for {upload.Type}.");

            await using var file = await DownloadAndVerifyAsync(upload, ct);

            datasetId = await datasets.CreateStagingAsync(job.CompanyId, job.Id, ct);
            var result = await processor.ImportAsync(file, datasetId.Value, ct);
            bool activated = await datasets.ActivateAsync(datasetId.Value, ct);

            await heartbeat.DisposeAsync();
            job.Status = JobStatus.Succeeded;
            job.DatasetId = datasetId;
            job.ErrorCode = null;
            job.ErrorDetail = activated ? null : "A newer upload was activated first; this dataset was not activated.";
            job.FinishedAt = clock.GetUtcNow();
            await SetInvitationStatusAsync(upload.InvitationId, InvitationStatus.Processed, ct);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Job {JobId} succeeded in {Duration}: {JournalLines} journal lines, {Accounts} accounts",
                job.Id, job.FinishedAt - job.StartedAt, result.JournalLines, result.Accounts);
            return JobOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: hand the job back so the redelivered message can claim it immediately.
            await heartbeat.DisposeAsync();
            if (datasetId is { } id) await datasets.MarkFailedAsync(id, CancellationToken.None);
            job.Status = JobStatus.Queued;
            job.Attempts--;
            job.QueuedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Job {JobId} interrupted by shutdown and returned to the queue", job.Id);
            throw;
        }
        catch (Exception ex)
        {
            await heartbeat.DisposeAsync();
            if (datasetId is { } id) await datasets.MarkFailedAsync(id, CancellationToken.None);
            return await HandleFailureAsync(job, upload, ex, ct);
        }
    }

    private async Task<bool> TryClaimAsync(Guid jobId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var staleBefore = now - options.Value.StaleAfter;
        int claimed = await db.ProcessingJobs
            .Where(j => j.Id == jobId
                && (j.Status == JobStatus.Queued || (j.Status == JobStatus.Processing && j.HeartbeatAt < staleBefore)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Processing)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.HeartbeatAt, now), ct);
        return claimed == 1;
    }

    /// <summary>Downloads to a temp file (deleted on dispose) and checks size and SHA-256 against the upload record.</summary>
    private async Task<FileStream> DownloadAndVerifyAsync(Upload upload, CancellationToken ct)
    {
        var o = options.Value;
        Directory.CreateDirectory(o.TempDirectory);
        var file = new FileStream(Path.Combine(o.TempDirectory, $"{upload.Id:N}-{Guid.NewGuid():N}.upload"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await files.DownloadAsync(upload.S3Key, file, ct);
            if (file.Length != upload.SizeBytes)
                throw new DataHubException(ErrorKind.Validation, ProcessingErrors.Corrupted,
                    $"Downloaded {file.Length} bytes, expected {upload.SizeBytes}.");

            file.Position = 0;
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (upload.Sha256 is null)
                upload.Sha256 = sha256; // the browser does not hash multi-GB files; record it now
            else if (upload.Sha256 != sha256)
                throw new DataHubException(ErrorKind.Validation, ProcessingErrors.Corrupted,
                    $"SHA-256 mismatch: expected {upload.Sha256}, got {sha256}.");

            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private async Task<JobOutcome> HandleFailureAsync(ProcessingJob job, Upload upload, Exception ex, CancellationToken ct)
    {
        string? code = ex switch
        {
            DataHubException d => d.Code,
            OrisFileException => ProcessingErrors.Unreadable,
            InvalidDataException => ProcessingErrors.NotZip, // corrupt ZIP structure
            _ => null,
        };

        if (code is null && job.Attempts < options.Value.MaxAttempts)
        {
            // Transient (database, S3, network): back to Queued; the recovery sweep publishes it again later.
            job.Status = JobStatus.Queued;
            job.QueuedAt = clock.GetUtcNow();
            job.ErrorDetail = Describe(ex);
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(ex, "Job {JobId} attempt {Attempt} of {MaxAttempts} failed; retry scheduled",
                job.Id, job.Attempts, options.Value.MaxAttempts);
            return JobOutcome.RetryScheduled;
        }

        code ??= ProcessingErrors.Failed;
        await FailAsync(job, upload, code, Describe(ex), ct);
        if (code == ProcessingErrors.Failed)
            logger.LogError(ex, "Job {JobId} failed after {Attempts} attempts", job.Id, job.Attempts);
        else
            logger.LogWarning("Job {JobId} rejected: {ErrorCode} ({Detail})", job.Id, code, ex.Message);
        return JobOutcome.Failed;
    }

    /// <summary>Marks the job failed, updates the invitation for LOS, and tells the customer.</summary>
    internal async Task FailAsync(ProcessingJob job, Upload upload, string code, string detail, CancellationToken ct)
    {
        job.Status = JobStatus.Failed;
        job.ErrorCode = code;
        job.ErrorDetail = detail;
        job.FinishedAt = clock.GetUtcNow();
        await SetInvitationStatusAsync(upload.InvitationId, InvitationStatus.Failed, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.ProcessingFailedAsync(upload.InvitationId, code, ct);
    }

    private async Task SetInvitationStatusAsync(Guid invitationId, InvitationStatus status, CancellationToken ct)
    {
        var invitation = await db.Invitations.SingleAsync(i => i.Id == invitationId, ct);
        invitation.Status = status;
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>Refreshes the job's heartbeat on a separate connection while the import runs.</summary>
    private sealed class Heartbeat : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Heartbeat(Guid jobId, IServiceScopeFactory scopes, TimeSpan interval, TimeProvider clock, ILogger logger) =>
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(interval, clock, _stop.Token);
                        await using var scope = scopes.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<IDataHubDb>();
                        var now = clock.GetUtcNow();
                        await db.ProcessingJobs.Where(j => j.Id == jobId && j.Status == JobStatus.Processing)
                            .ExecuteUpdateAsync(s => s.SetProperty(j => j.HeartbeatAt, now), _stop.Token);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Heartbeat for job {JobId} failed", jobId);
                    }
                }
            });

        public async ValueTask DisposeAsync()
        {
            if (_stop.IsCancellationRequested) return;
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
        }
    }
}
