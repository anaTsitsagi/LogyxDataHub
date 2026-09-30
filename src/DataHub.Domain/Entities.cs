namespace DataHub.Domain;

/// <summary>A customer company. <see cref="TenantId"/> is the X-Tenant-Id TBC systems use in API calls.</summary>
public class Company
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Company identification (tax) code, as known to TBC LOS.</summary>
    public string CompanyCode { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The dataset the APIs serve. Null until the first successful processing.</summary>
    public Guid? ActiveDatasetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A unique upload link created by TBC LOS and sent to the customer by SMS and/or email.</summary>
public class Invitation
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>SHA-256 of the link token; the token itself is never stored.</summary>
    public byte[] TokenHash { get; set; } = [];
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public NotificationChannels Channels { get; set; }
    public InvitationStatus Status { get; set; }

    /// <summary>Client-supplied Idempotency-Key, unique per API client, so LOS retries don't duplicate.</summary>
    public string? IdempotencyKey { get; set; }
    public string? CreatedByClient { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

/// <summary>A one-time verification code sent to the customer.</summary>
public class OtpChallenge
{
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public byte[] CodeHash { get; set; } = [];
    public NotificationChannels Channel { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>A file uploaded by the customer and stored in S3.</summary>
public class Upload
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid InvitationId { get; set; }
    public UploadType Type { get; set; }
    public UploadStatus Status { get; set; }
    public string FileName { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string S3Key { get; set; } = "";

    /// <summary>S3 multipart upload id while the upload is in progress.</summary>
    public string? S3UploadId { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Processing of one upload by the worker.</summary>
public class ProcessingJob
{
    public Guid Id { get; set; }
    public Guid UploadId { get; set; }
    public Guid CompanyId { get; set; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }

    /// <summary>Stable code for the user-facing message (e.g. <c>ORIS_ENCRYPTED</c>).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Internal diagnostic detail; never shown to the customer.</summary>
    public string? ErrorDetail { get; set; }
    public Guid? DatasetId { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>One version of a company's accounting data. Only the Active one is served.</summary>
public class Dataset
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProcessingJobId { get; set; }
    public DatasetStatus Status { get; set; }
    public int AccountCount { get; set; }
    public int JournalEntryCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
}

/// <summary>Chart-of-accounts entry within a dataset.</summary>
public class Account
{
    public long Id { get; set; }
    public Guid DatasetId { get; set; }

    /// <summary>4-digit balance account, e.g. <c>3120</c>.</summary>
    public string Code { get; set; } = "";

    /// <summary>Nested sub-account path, e.g. <c>1 193</c>; empty for the balance account itself.</summary>
    public string Sub { get; set; } = "";
    public string Raw { get; set; } = "";
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public string? Currency { get; set; }
}

/// <summary>A journal line within a dataset (ORIS WIRING).</summary>
public class JournalEntry
{
    public long Id { get; set; }
    public Guid DatasetId { get; set; }
    public int RecordNumber { get; set; }
    public string DocumentNumber { get; set; } = "";
    public long EntryNumber { get; set; }
    public string? Debet { get; set; }
    public string DebetSub { get; set; } = "";
    public string DebetRaw { get; set; } = "";
    public string? Credit { get; set; }
    public string CreditSub { get; set; } = "";
    public string CreditRaw { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal? Quantity { get; set; }
    public string Unit { get; set; } = "";
    public string PostedBy { get; set; } = "";
    public DateOnly? OperationDate { get; set; }
    public DateOnly? PostingDate { get; set; }
}
