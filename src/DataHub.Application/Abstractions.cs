using DataHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Application;

/// <summary>Data access used by application services; implemented by the EF Core context.</summary>
public interface IDataHubDb
{
    DbSet<Company> Companies { get; }
    DbSet<Invitation> Invitations { get; }
    DbSet<OtpChallenge> OtpChallenges { get; }
    DbSet<Upload> Uploads { get; }
    DbSet<ProcessingJob> ProcessingJobs { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface ISmsSender
{
    Task SendAsync(string phone, string text, CancellationToken ct);
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct);
}

public sealed record StoredPart(int PartNumber, long Size, string ETag);

/// <summary>Object storage for uploaded files (AWS S3 or compatible).</summary>
public interface IFileStore
{
    string Bucket { get; }
    Task<string> StartMultipartAsync(string key, string contentType, CancellationToken ct);
    Task UploadPartAsync(string key, string uploadId, int partNumber, Stream content, long length, CancellationToken ct);
    Task<IReadOnlyList<StoredPart>> ListPartsAsync(string key, string uploadId, CancellationToken ct);
    Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<StoredPart> parts, CancellationToken ct);
    Task AbortMultipartAsync(string key, string uploadId, CancellationToken ct);

    /// <summary>Opens a seekable read stream (served with ranged reads, so large files are never fully downloaded).</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>Streams the whole object into <paramref name="destination"/> in a single sequential download.</summary>
    Task DownloadAsync(string key, Stream destination, CancellationToken ct);
}

/// <summary>
/// Message placed on the processing queue. It carries only the file's location, never the file itself.
/// </summary>
public sealed record ProcessingJobMessage(
    Guid JobId,
    Guid UploadId,
    Guid CompanyId,
    Guid TenantId,
    UploadType UploadType,
    string S3Bucket,
    string S3Key,
    long SizeBytes,
    string? Sha256,
    string CorrelationId)
{
    public static ProcessingJobMessage For(ProcessingJob job, Upload upload, Guid tenantId, string correlationId) =>
        new(job.Id, upload.Id, upload.CompanyId, tenantId, upload.Type, upload.S3Bucket, upload.S3Key,
            upload.SizeBytes, upload.Sha256, correlationId);
}

public interface IJobPublisher
{
    Task PublishAsync(ProcessingJobMessage message, CancellationToken ct);
}

public enum ErrorKind { Validation, NotFound, Forbidden, Conflict, TooManyRequests }

/// <summary>An expected business failure. <see cref="Code"/> is stable and safe to show to callers.</summary>
public sealed class DataHubException(ErrorKind kind, string code, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
}
