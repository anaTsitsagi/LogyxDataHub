using System.IO.Compression;
using DataHub.Application.Datasets;
using DataHub.Application.Uploads;
using DataHub.Domain;
using DataHub.Oris;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Processing;

public sealed record ImportResult(int Accounts, int JournalLines);

/// <summary>Turns one kind of uploaded file into rows of a staging dataset.</summary>
/// <remarks>The ORIS entries CSV (later phase) is added as another implementation.</remarks>
public interface IUploadProcessor
{
    UploadType Type { get; }

    /// <exception cref="DataHubException">The content is invalid; retrying will not help.</exception>
    Task<ImportResult> ImportAsync(Stream file, Guid datasetId, CancellationToken ct);
}

/// <summary>
/// Imports a ZIP of ORIS 5 .tps tables. Tables are extracted to disk one at a time, and rows are
/// streamed into the database, so memory use does not grow with the archive size.
/// </summary>
public sealed class OrisDatabaseProcessor(
    IDatasetStore datasets,
    IOptions<ProcessingOptions> options,
    IOptions<UploadOptions> limits,
    ILogger<OrisDatabaseProcessor> logger) : IUploadProcessor
{
    public UploadType Type => UploadType.OrisDatabase;

    public async Task<ImportResult> ImportAsync(Stream file, Guid datasetId, CancellationToken ct)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);

        var journals = zip.Entries.Where(e => IsFile(e, OrisReader.JournalFile)).ToList();
        if (journals.Count == 0) throw Invalid(ProcessingErrors.NoJournal, "WIRING.TPS not found in the archive.");
        if (journals.Count > 1)
            throw Invalid(ProcessingErrors.MultipleDatabases, $"{journals.Count} WIRING.TPS files found in the archive.");

        var journal = journals[0];
        var folder = FolderOf(journal);
        var accountNames = zip.Entries.FirstOrDefault(e => IsFile(e, OrisReader.AccountNamesFile) && FolderOf(e) == folder);

        int accounts = 0;
        if (accountNames is null)
        {
            logger.LogWarning("Acc_name.tps not found next to WIRING.TPS; account names will be empty");
        }
        else
        {
            await using var table = await ExtractAsync(accountNames, ct);
            accounts = await datasets.WriteAccountsAsync(datasetId, OrisReader.ReadAccountNames(table, accountNames.Name), ct);
        }

        int lines;
        await using (var table = await ExtractAsync(journal, ct))
            lines = await datasets.WriteJournalAsync(datasetId, OrisReader.ReadJournalLines(table, journal.Name), ct);

        if (lines == 0) throw Invalid(ProcessingErrors.EmptyJournal, "WIRING.TPS contains no records.");

        logger.LogInformation("Imported {JournalLines} journal lines and {Accounts} accounts into dataset {DatasetId}",
            lines, accounts, datasetId);
        return new ImportResult(accounts, lines);
    }

    /// <summary>Copies one entry to a temp file that is deleted when disposed; the TPS reader needs a seekable stream.</summary>
    private async Task<FileStream> ExtractAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        long limit = limits.Value.MaxUncompressedBytes;
        if (entry.Length > limit)
            throw Invalid(ProcessingErrors.Unreadable, $"{entry.FullName} expands to {entry.Length} bytes, above the limit.");

        Directory.CreateDirectory(options.Value.TempDirectory);
        var target = new FileStream(Path.Combine(options.Value.TempDirectory, $"{Guid.NewGuid():N}.tps"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await using var source = await entry.OpenAsync(ct);
            await source.CopyToAsync(target, 1024 * 1024, ct);
            // Declared sizes can lie; ZipArchive validates the length, but never trust more than the limit.
            if (target.Length > limit)
                throw Invalid(ProcessingErrors.Unreadable, $"{entry.FullName} is larger than its declared size.");
            target.Position = 0;
            return target;
        }
        catch
        {
            await target.DisposeAsync();
            throw;
        }
    }

    private static bool IsFile(ZipArchiveEntry entry, string name) => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string FolderOf(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/')[..^entry.Name.Length];

    private static DataHubException Invalid(string code, string message) => new(ErrorKind.Validation, code, message);
}
