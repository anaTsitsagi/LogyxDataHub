using System.Data;
using DataHub.Application.Datasets;
using DataHub.Domain;
using DataHub.Oris;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataHub.Infrastructure.Persistence;

public sealed class SqlDatasetStore(DataHubDbContext db, ILogger<SqlDatasetStore> logger, TimeProvider clock) : IDatasetStore
{
    // Rows buffered per SqlBulkCopy round-trip; bounds worker memory regardless of file size.
    internal const int BulkBatchSize = 10_000;

    public async Task<Guid> CreateStagingAsync(Guid companyId, Guid processingJobId, CancellationToken ct)
    {
        var dataset = new Dataset
        {
            Id = Guid.CreateVersion7(),
            CompanyId = companyId,
            ProcessingJobId = processingJobId,
            Status = DatasetStatus.Staging,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Datasets.Add(dataset);
        await db.SaveChangesAsync(ct);
        // Counts are later set with ExecuteUpdate, which bypasses the change tracker; don't keep a stale copy.
        db.Entry(dataset).State = EntityState.Detached;
        return dataset.Id;
    }

    public async Task<int> WriteAccountsAsync(Guid datasetId, IEnumerable<OrisAccountName> accounts, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add(nameof(Account.DatasetId), typeof(Guid));
        table.Columns.Add(nameof(Account.Code), typeof(string));
        table.Columns.Add(nameof(Account.Sub), typeof(string));
        table.Columns.Add(nameof(Account.Raw), typeof(string));
        table.Columns.Add(nameof(Account.Level), typeof(int));
        table.Columns.Add(nameof(Account.Name), typeof(string));
        table.Columns.Add(nameof(Account.Currency), typeof(string));

        int total = await BulkWriteAsync("Accounts", table, accounts
            .Where(a => a.Account is not null)
            .Select(a => new object?[]
            {
                datasetId, a.Account!.Code, a.Account.Sub, a.Raw, a.Level, a.Name,
                string.IsNullOrEmpty(a.Currency) ? null : a.Currency,
            }), ct);

        await db.Datasets.Where(d => d.Id == datasetId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AccountCount, total), ct);
        return total;
    }

    public async Task<int> WriteJournalAsync(Guid datasetId, IEnumerable<OrisJournalLine> lines, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add(nameof(JournalEntry.DatasetId), typeof(Guid));
        table.Columns.Add(nameof(JournalEntry.RecordNumber), typeof(int));
        table.Columns.Add(nameof(JournalEntry.DocumentNumber), typeof(string));
        table.Columns.Add(nameof(JournalEntry.EntryNumber), typeof(long));
        table.Columns.Add(nameof(JournalEntry.Debet), typeof(string));
        table.Columns.Add(nameof(JournalEntry.DebetSub), typeof(string));
        table.Columns.Add(nameof(JournalEntry.DebetRaw), typeof(string));
        table.Columns.Add(nameof(JournalEntry.Credit), typeof(string));
        table.Columns.Add(nameof(JournalEntry.CreditSub), typeof(string));
        table.Columns.Add(nameof(JournalEntry.CreditRaw), typeof(string));
        table.Columns.Add(nameof(JournalEntry.Amount), typeof(decimal));
        table.Columns.Add(nameof(JournalEntry.Currency), typeof(string));
        table.Columns.Add(nameof(JournalEntry.AmountGel), typeof(decimal));
        table.Columns.Add(nameof(JournalEntry.ExchangeRate), typeof(decimal));
        table.Columns.Add(nameof(JournalEntry.Description), typeof(string));
        table.Columns.Add(nameof(JournalEntry.Quantity), typeof(decimal));
        table.Columns.Add(nameof(JournalEntry.Unit), typeof(string));
        table.Columns.Add(nameof(JournalEntry.PostedBy), typeof(string));
        table.Columns.Add(nameof(JournalEntry.OperationDate), typeof(DateTime));
        table.Columns.Add(nameof(JournalEntry.PostingDate), typeof(DateTime));

        int total = await BulkWriteAsync("JournalEntries", table, lines.Select(l => new object?[]
        {
            datasetId, l.RecordNumber, l.DocumentNumber, l.EntryNumber,
            l.Debit?.Code, l.Debit?.Sub ?? "", l.DebitRaw,
            l.Credit?.Code, l.Credit?.Sub ?? "", l.CreditRaw,
            l.Amount, l.Currency, GelAmount(l), l.ExchangeRate, l.Description, l.Quantity, l.Unit, l.PostedBy,
            l.OperationDate?.ToDateTime(TimeOnly.MinValue), l.PostingDate?.ToDateTime(TimeOnly.MinValue),
        }), ct);

        await db.Datasets.Where(d => d.Id == datasetId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.JournalEntryCount, total), ct);
        return total;
    }

    private static decimal GelAmount(OrisJournalLine line) =>
        line.AmountGel ?? (GelConverter.IsNational(line.Currency)
            ? line.Amount
            : throw new InvalidOperationException($"Record {line.RecordNumber} is in {line.Currency} but has no GEL amount; apply GelConverter first."));

    public async Task<bool> ActivateAsync(Guid datasetId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            var dataset = await db.Datasets.SingleAsync(d => d.Id == datasetId, ct);
            if (dataset.Status != DatasetStatus.Staging)
                throw new InvalidOperationException($"Dataset {datasetId} is {dataset.Status}, expected Staging.");

            // Lock the company row so concurrent activations for the same company serialize.
            var company = await db.Companies
                .FromSql($"SELECT * FROM [datahub].[Companies] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {dataset.CompanyId}")
                .SingleAsync(ct);

            var current = company.ActiveDatasetId is { } activeId
                ? await db.Datasets.SingleAsync(d => d.Id == activeId, ct)
                : null;

            var now = clock.GetUtcNow();
            if (current is not null && current.CreatedAt > dataset.CreatedAt)
            {
                // A newer upload finished first; never replace newer data with older data.
                dataset.Status = DatasetStatus.Superseded;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                logger.LogWarning("Dataset {DatasetId} not activated: newer dataset {ActiveDatasetId} is already active", datasetId, current.Id);
                return false;
            }

            if (current is not null) current.Status = DatasetStatus.Superseded;
            dataset.Status = DatasetStatus.Active;
            dataset.ActivatedAt = now;
            company.ActiveDatasetId = dataset.Id;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("Dataset {DatasetId} activated for company {CompanyId}, replacing {PreviousDatasetId}",
                datasetId, company.Id, current?.Id);
            return true;
        });
    }

    public async Task MarkFailedAsync(Guid datasetId, CancellationToken ct) =>
        await db.Datasets.Where(d => d.Id == datasetId && d.Status == DatasetStatus.Staging)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DatasetStatus.Failed), ct);

    public async Task AbandonStagingAsync(Guid processingJobId, CancellationToken ct) =>
        await db.Datasets.Where(d => d.ProcessingJobId == processingJobId && d.Status == DatasetStatus.Staging)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DatasetStatus.Failed), ct);

    public async Task<long> PurgeInactiveAsync(CancellationToken ct)
    {
        var inactive = await db.Datasets
            .Where(d => d.Status == DatasetStatus.Superseded || d.Status == DatasetStatus.Failed)
            .Select(d => d.Id)
            .ToListAsync(ct);

        long deleted = 0;
        foreach (var id in inactive)
        {
            deleted += await DeleteInBatchesAsync(() =>
                db.Database.ExecuteSqlAsync($"DELETE TOP (50000) FROM [datahub].[JournalEntries] WHERE [DatasetId] = {id}", ct));
            deleted += await DeleteInBatchesAsync(() =>
                db.Database.ExecuteSqlAsync($"DELETE TOP (50000) FROM [datahub].[Accounts] WHERE [DatasetId] = {id}", ct));
            await db.Datasets.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        }
        return deleted;
    }

    // Small batches keep lock escalation and transaction log growth in check.
    private static async Task<long> DeleteInBatchesAsync(Func<Task<int>> deleteBatch)
    {
        long total = 0;
        int affected;
        do
        {
            affected = await deleteBatch();
            total += affected;
        } while (affected > 0);
        return total;
    }

    private async Task<int> BulkWriteAsync(string tableName, DataTable buffer, IEnumerable<object?[]> rows, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);

        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null)
        {
            DestinationTableName = $"[{DataHubDbContext.Schema}].[{tableName}]",
            BulkCopyTimeout = 0,
        };
        foreach (DataColumn column in buffer.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);

        int total = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            buffer.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
            if (buffer.Rows.Count >= BulkBatchSize)
            {
                total += buffer.Rows.Count;
                await bulk.WriteToServerAsync(buffer, ct);
                buffer.Clear();
            }
        }
        if (buffer.Rows.Count > 0)
        {
            total += buffer.Rows.Count;
            await bulk.WriteToServerAsync(buffer, ct);
            buffer.Clear();
        }
        return total;
    }
}
