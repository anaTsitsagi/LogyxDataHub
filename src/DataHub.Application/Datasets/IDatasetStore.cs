using DataHub.Oris;

namespace DataHub.Application.Datasets;

/// <summary>
/// Writes processed ORIS data as a new dataset and swaps it in only when processing succeeds.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="CreateStagingAsync"/> → write accounts and journal → <see cref="ActivateAsync"/>.
/// If anything fails, call <see cref="MarkFailedAsync"/>; the company's current data stays untouched.
/// Rows of superseded and failed datasets are removed later by <see cref="PurgeInactiveAsync"/>.
/// </remarks>
public interface IDatasetStore
{
    Task<Guid> CreateStagingAsync(Guid companyId, Guid processingJobId, CancellationToken ct);

    Task<int> WriteAccountsAsync(Guid datasetId, IEnumerable<OrisAccountName> accounts, CancellationToken ct);

    Task<int> WriteJournalAsync(Guid datasetId, IEnumerable<OrisJournalLine> lines, CancellationToken ct);

    /// <summary>
    /// Makes the staging dataset the company's active one in a single transaction.
    /// Returns false (and marks it superseded) when a newer dataset was activated meanwhile.
    /// </summary>
    Task<bool> ActivateAsync(Guid datasetId, CancellationToken ct);

    Task MarkFailedAsync(Guid datasetId, CancellationToken ct);

    /// <summary>Marks staging datasets left behind by an interrupted attempt of this job as failed.</summary>
    Task AbandonStagingAsync(Guid processingJobId, CancellationToken ct);

    /// <summary>Deletes rows of superseded/failed datasets in batches. Returns the number of rows deleted.</summary>
    Task<long> PurgeInactiveAsync(CancellationToken ct);
}
