using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DataHub.Application.Verification;
using DataHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Uploads;

public sealed class UploadOptions
{
    public const string Section = "Uploads";

    public long MaxSizeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Chunk size the browser sends; each chunk becomes one S3 part (S3 minimum is 5 MB).</summary>
    public int ChunkSizeBytes { get; set; } = 16 * 1024 * 1024;

    public long MaxUncompressedBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public int MaxZipEntries { get; set; } = 5_000;
    public int MaxCompressionRatio { get; set; } = 200;

    /// <summary>ORIS entries CSV upload is a later phase; the portal shows it disabled until enabled here.</summary>
    public bool EntriesCsvEnabled { get; set; }
}

public sealed record StartUploadResult(Guid UploadId, int ChunkSizeBytes, int PartCount);

public sealed record UploadProgress(Guid UploadId, int PartCount, IReadOnlyList<int> CompletedParts);

public sealed record CustomerStatus(
    Guid? UploadId,
    string? FileName,
    UploadType? UploadType,
    UploadStatus? UploadStatus,
    JobStatus? JobStatus,
    string? ErrorCode,
    DateTimeOffset? UpdatedAt);

public sealed partial class UploadService(
    IDataHubDb db,
    IFileStore files,
    IJobPublisher publisher,
    IOptions<UploadOptions> options,
    TimeProvider clock,
    ILogger<UploadService> logger)
{
    public async Task<StartUploadResult> StartAsync(VerifiedCustomer customer, UploadType type, string fileName, long sizeBytes, CancellationToken ct)
    {
        var o = options.Value;
        if (type == UploadType.OrisEntriesCsv && !o.EntriesCsvEnabled)
            throw Invalid("UPLOAD_TYPE_DISABLED", "Uploading the ORIS entries sheet is not available yet.");

        var expectedExtension = type == UploadType.OrisDatabase ? ".zip" : ".csv";
        if (!string.Equals(Path.GetExtension(fileName), expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw Invalid("UPLOAD_WRONG_EXTENSION", $"Please select a {expectedExtension} file.");
        if (sizeBytes <= 0 || sizeBytes > o.MaxSizeBytes)
            throw Invalid("UPLOAD_TOO_LARGE", $"The file must be between 1 byte and {o.MaxSizeBytes / (1024 * 1024)} MB.");

        await EnsureNoActiveProcessingAsync(customer, ct);

        var uploadId = Guid.CreateVersion7();
        var key = $"uploads/{customer.TenantId:N}/{uploadId:N}/{SafeKeyName(fileName)}";
        var s3UploadId = await files.StartMultipartAsync(key, type == UploadType.OrisDatabase ? "application/zip" : "text/csv", ct);

        db.Uploads.Add(new Upload
        {
            Id = uploadId,
            CompanyId = customer.CompanyId,
            InvitationId = customer.InvitationId,
            Type = type,
            Status = UploadStatus.InProgress,
            FileName = Path.GetFileName(fileName),
            S3Bucket = files.Bucket,
            S3Key = key,
            S3UploadId = s3UploadId,
            SizeBytes = sizeBytes,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Upload {UploadId} started for tenant {TenantId}: {SizeBytes} bytes, type {UploadType}",
            uploadId, customer.TenantId, sizeBytes, type);
        return new StartUploadResult(uploadId, o.ChunkSizeBytes, PartCount(sizeBytes));
    }

    public async Task UploadPartAsync(VerifiedCustomer customer, Guid uploadId, int partNumber, Stream content, long length, CancellationToken ct)
    {
        var upload = await GetInProgressAsync(customer, uploadId, ct);
        int parts = PartCount(upload.SizeBytes);
        if (partNumber < 1 || partNumber > parts)
            throw Invalid("UPLOAD_BAD_PART", $"Part number must be between 1 and {parts}.");

        long expected = partNumber < parts
            ? options.Value.ChunkSizeBytes
            : upload.SizeBytes - (long)(parts - 1) * options.Value.ChunkSizeBytes;
        if (length != expected)
            throw Invalid("UPLOAD_BAD_PART_SIZE", $"Part {partNumber} must be exactly {expected} bytes.");

        await files.UploadPartAsync(upload.S3Key, upload.S3UploadId!, partNumber, content, length, ct);
    }

    /// <summary>Which parts are already stored, so an interrupted upload resumes instead of restarting.</summary>
    public async Task<UploadProgress> GetProgressAsync(VerifiedCustomer customer, Guid uploadId, CancellationToken ct)
    {
        var upload = await GetInProgressAsync(customer, uploadId, ct);
        var stored = await files.ListPartsAsync(upload.S3Key, upload.S3UploadId!, ct);
        return new UploadProgress(upload.Id, PartCount(upload.SizeBytes), stored.Select(p => p.PartNumber).Order().ToList());
    }

    public async Task<Guid> CompleteAsync(VerifiedCustomer customer, Guid uploadId, string? sha256, CancellationToken ct)
    {
        var upload = await GetInProgressAsync(customer, uploadId, ct);
        var stored = await files.ListPartsAsync(upload.S3Key, upload.S3UploadId!, ct);
        int parts = PartCount(upload.SizeBytes);
        if (stored.Count != parts || stored.Sum(p => p.Size) != upload.SizeBytes)
            throw Invalid("UPLOAD_INCOMPLETE", $"{stored.Count} of {parts} parts received. Please resume the upload.");

        await files.CompleteMultipartAsync(upload.S3Key, upload.S3UploadId!, stored.OrderBy(p => p.PartNumber).ToList(), ct);
        upload.S3UploadId = null;
        upload.Sha256 = sha256 is { Length: 64 } && sha256.All(Uri.IsHexDigit) ? sha256.ToLowerInvariant() : null;

        try
        {
            if (upload.Type == UploadType.OrisDatabase)
            {
                await using var stream = await files.OpenReadAsync(upload.S3Key, ct);
                var summary = ZipInspector.Inspect(stream, options.Value);
                logger.LogInformation("Upload {UploadId} contains {TpsCount} .tps files ({Uncompressed} bytes uncompressed)",
                    upload.Id, summary.TpsFileCount, summary.TotalUncompressedBytes);
            }
        }
        catch (DataHubException ex)
        {
            upload.Status = UploadStatus.Rejected;
            upload.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Upload {UploadId} rejected: {ErrorCode}", upload.Id, ex.Code);
            throw;
        }

        var now = clock.GetUtcNow();
        upload.Status = UploadStatus.Completed;
        upload.CompletedAt = now;

        var job = new ProcessingJob
        {
            Id = Guid.CreateVersion7(),
            UploadId = upload.Id,
            CompanyId = upload.CompanyId,
            Status = JobStatus.Queued,
            QueuedAt = now,
        };
        db.ProcessingJobs.Add(job);

        var invitation = await db.Invitations.SingleAsync(i => i.Id == customer.InvitationId, ct);
        invitation.Status = InvitationStatus.Uploaded;
        await db.SaveChangesAsync(ct);

        // The job row is committed first. If publishing fails, the worker's re-queue sweep picks it up.
        try
        {
            await publisher.PublishAsync(ProcessingJobMessage.For(job, upload, customer.TenantId,
                Activity.Current?.TraceId.ToString() ?? job.Id.ToString("N")), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Publishing job {JobId} failed; it will be re-queued by the sweep", job.Id);
        }

        var typeTag = new KeyValuePair<string, object?>("upload.type", upload.Type.ToString());
        DataHubTelemetry.UploadsCompleted.Add(1, typeTag);
        DataHubTelemetry.UploadedBytes.Add(upload.SizeBytes, typeTag);
        logger.LogInformation("Upload {UploadId} completed; job {JobId} queued", upload.Id, job.Id);
        return job.Id;
    }

    public async Task AbortAsync(VerifiedCustomer customer, Guid uploadId, CancellationToken ct)
    {
        var upload = await GetInProgressAsync(customer, uploadId, ct);
        await files.AbortMultipartAsync(upload.S3Key, upload.S3UploadId!, ct);
        upload.Status = UploadStatus.Aborted;
        upload.S3UploadId = null;
        upload.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<CustomerStatus> GetStatusAsync(VerifiedCustomer customer, CancellationToken ct)
    {
        var upload = await db.Uploads.AsNoTracking()
            .Where(u => u.InvitationId == customer.InvitationId && u.Status != UploadStatus.Aborted)
            .OrderByDescending(u => u.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (upload is null) return new CustomerStatus(null, null, null, null, null, null, null);

        var job = await db.ProcessingJobs.AsNoTracking()
            .Where(j => j.UploadId == upload.Id)
            .OrderByDescending(j => j.QueuedAt)
            .FirstOrDefaultAsync(ct);

        return new CustomerStatus(upload.Id, upload.FileName, upload.Type, upload.Status, job?.Status, job?.ErrorCode,
            job?.FinishedAt ?? job?.StartedAt ?? job?.QueuedAt ?? upload.CompletedAt ?? upload.CreatedAt);
    }

    private async Task EnsureNoActiveProcessingAsync(VerifiedCustomer customer, CancellationToken ct)
    {
        bool busy = await db.ProcessingJobs.AnyAsync(j => j.CompanyId == customer.CompanyId
            && (j.Status == JobStatus.Queued || j.Status == JobStatus.Processing), ct);
        if (busy)
            throw new DataHubException(ErrorKind.Conflict, "UPLOAD_PROCESSING", "A previous upload is still being processed.");
    }

    private async Task<Upload> GetInProgressAsync(VerifiedCustomer customer, Guid uploadId, CancellationToken ct)
    {
        var upload = await db.Uploads.SingleOrDefaultAsync(u => u.Id == uploadId && u.InvitationId == customer.InvitationId, ct)
            ?? throw new DataHubException(ErrorKind.NotFound, "UPLOAD_NOT_FOUND", "Upload not found.");
        if (upload.Status != UploadStatus.InProgress || upload.S3UploadId is null)
            throw new DataHubException(ErrorKind.Conflict, "UPLOAD_NOT_IN_PROGRESS", "This upload is no longer in progress.");
        return upload;
    }

    private int PartCount(long size) => (int)((size + options.Value.ChunkSizeBytes - 1) / options.Value.ChunkSizeBytes);

    /// <summary>ASCII-only object key segment; the original (possibly Georgian) name is kept in the database.</summary>
    internal static string SafeKeyName(string fileName)
    {
        var name = UnsafeKeyChars().Replace(Path.GetFileName(fileName).Normalize(NormalizationForm.FormKC), "_");
        name = name.Trim('.', '_');
        if (name.Length > 100) name = name[^100..];
        return name.Length == 0 ? "upload" : name;
    }

    private static DataHubException Invalid(string code, string message) => new(ErrorKind.Validation, code, message);

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeKeyChars();
}
