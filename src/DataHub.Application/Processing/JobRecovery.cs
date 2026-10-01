using DataHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Processing;

public sealed record RecoveryResult(int Requeued, int Republished, int Failed);

/// <summary>
/// Periodic sweep that makes processing self-healing. It republishes jobs whose message was lost
/// or that are waiting for a retry, and returns jobs abandoned by a crashed worker to the queue.
/// </summary>
/// <remarks>Safe to run in several worker replicas at once: duplicate messages are skipped when claimed.</remarks>
public sealed class JobRecovery(
    IDataHubDb db,
    IJobPublisher publisher,
    JobProcessor processor,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<JobRecovery> logger)
{
    private const int BatchSize = 100;

    public async Task<RecoveryResult> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        int requeued = 0, failed = 0, republished = 0;

        var staleBefore = now - o.StaleAfter;
        var abandoned = await db.ProcessingJobs
            .Where(j => j.Status == JobStatus.Processing && j.HeartbeatAt < staleBefore)
            .OrderBy(j => j.HeartbeatAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var job in abandoned)
        {
            if (job.Attempts >= o.MaxAttempts)
            {
                var upload = await db.Uploads.SingleAsync(u => u.Id == job.UploadId, ct);
                await processor.FailAsync(job, upload, ProcessingErrors.Failed, "Worker stopped responding on the last attempt.", ct);
                logger.LogError("Job {JobId} failed: worker stopped responding on attempt {Attempt}", job.Id, job.Attempts);
                failed++;
            }
            else
            {
                job.Status = JobStatus.Queued;
                job.QueuedAt = now - o.RequeueAfter; // due for republishing below
                logger.LogWarning("Job {JobId} abandoned by a worker (last heartbeat {HeartbeatAt}); returned to the queue", job.Id, job.HeartbeatAt);
                requeued++;
            }
        }
        await db.SaveChangesAsync(ct);

        var dueBefore = now - o.RequeueAfter;
        var due = await db.ProcessingJobs
            .Where(j => j.Status == JobStatus.Queued && j.QueuedAt <= dueBefore)
            .OrderBy(j => j.QueuedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var job in due)
        {
            var upload = await db.Uploads.AsNoTracking().SingleAsync(u => u.Id == job.UploadId, ct);
            var tenantId = await db.Companies.Where(c => c.Id == job.CompanyId).Select(c => c.TenantId).SingleAsync(ct);
            await publisher.PublishAsync(ProcessingJobMessage.For(job, upload, tenantId, job.Id.ToString("N")), ct);

            // Push the deadline out so each job is republished at most once per RequeueAfter.
            job.QueuedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Job {JobId} republished (queued since before {DueBefore})", job.Id, dueBefore);
            republished++;
        }

        return new RecoveryResult(requeued, republished, failed);
    }
}
