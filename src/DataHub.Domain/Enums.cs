namespace DataHub.Domain;

[Flags]
public enum NotificationChannels
{
    None = 0,
    Sms = 1,
    Email = 2,
}

public enum InvitationStatus
{
    Sent = 0,
    Verified = 1,
    Uploaded = 2,
    Processed = 3,
    Failed = 4,
    Expired = 5,
}

public enum UploadType
{
    /// <summary>ZIP containing ORIS .tps database files.</summary>
    OrisDatabase = 0,

    /// <summary>ORIS journal entries exported as CSV (later phase).</summary>
    OrisEntriesCsv = 1,
}

public enum UploadStatus
{
    InProgress = 0,
    Completed = 1,
    Rejected = 2,
    Aborted = 3,
}

public enum JobStatus
{
    Queued = 0,
    Processing = 1,
    Succeeded = 2,
    Failed = 3,
}

public enum DatasetStatus
{
    /// <summary>Being written by the worker; never visible to APIs.</summary>
    Staging = 0,

    /// <summary>The company's current data served by the APIs.</summary>
    Active = 1,

    /// <summary>Replaced by a newer dataset; rows are pending deletion.</summary>
    Superseded = 2,

    /// <summary>Processing failed; rows are pending deletion.</summary>
    Failed = 3,
}
