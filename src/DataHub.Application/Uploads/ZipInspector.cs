using System.IO.Compression;

namespace DataHub.Application.Uploads;

public sealed record ZipSummary(int TpsFileCount, long TotalUncompressedBytes, IReadOnlyList<string> TpsFiles);

/// <summary>
/// Validates an uploaded ZIP by reading only its central directory (a few KB via ranged reads),
/// so a 2 GB archive is checked without downloading it.
/// </summary>
public static class ZipInspector
{
    public static ZipSummary Inspect(Stream seekable, UploadOptions limits)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw Invalid("UPLOAD_NOT_ZIP", "The file is not a valid ZIP archive.");
        }

        using (archive)
        {
            if (archive.Entries.Count > limits.MaxZipEntries)
                throw Invalid("UPLOAD_TOO_MANY_FILES", $"The archive contains more than {limits.MaxZipEntries} files.");

            long total = 0;
            var tps = new List<string>();
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains("../") || name.Contains(':'))
                    throw Invalid("UPLOAD_UNSAFE_PATH", "The archive contains an unsafe file path.");

                total += entry.Length;
                if (total > limits.MaxUncompressedBytes)
                    throw Invalid("UPLOAD_TOO_LARGE_UNCOMPRESSED", "The archive expands to more data than allowed.");

                // Zip bomb guard: legitimate TPS data compresses well, but not a thousand-fold.
                if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                    throw Invalid("UPLOAD_SUSPICIOUS_COMPRESSION", "The archive has an unusual compression ratio.");

                if (name.EndsWith(".tps", StringComparison.OrdinalIgnoreCase))
                    tps.Add(name);
            }

            if (tps.Count == 0)
                throw Invalid("UPLOAD_NO_TPS", "The archive does not contain any ORIS .tps files.");

            return new ZipSummary(tps.Count, total, tps);
        }
    }

    private static DataHubException Invalid(string code, string message) => new(ErrorKind.Validation, code, message);
}
