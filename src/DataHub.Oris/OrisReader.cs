namespace DataHub.Oris;

/// <summary>One journal line from ORIS <c>WIRING.TPS</c>.</summary>
public sealed record OrisJournalLine(
    int RecordNumber,
    string DocumentNumber,
    long EntryNumber,
    OrisAccount? Debit,
    string DebitRaw,
    OrisAccount? Credit,
    string CreditRaw,
    decimal Amount,
    string Currency,
    string Description,
    decimal? Quantity,
    string Unit,
    string PostedBy,
    DateOnly? OperationDate,
    DateOnly? PostingDate)
{
    /// <summary>Exchange rate recorded on the line (ORIS CURS); null when ORIS left it to the daily rate table.</summary>
    public decimal? ExchangeRate { get; init; }

    /// <summary>Amount in GEL, as ORIS reports use it; set by <see cref="GelConverter"/>.</summary>
    public decimal? AmountGel { get; init; }
}

/// <summary>One chart-of-accounts entry from ORIS <c>Acc_name.tps</c>.</summary>
public sealed record OrisAccountName(OrisAccount? Account, string Raw, int Level, string Name, string Currency);

/// <summary>One daily exchange rate from ORIS <c>Rate.tps</c>: <see cref="Rate"/> GEL per 1 unit of the currency.</summary>
public sealed record OrisRate(string Currency, DateOnly Date, decimal Rate);

/// <summary>Maps ORIS 5 TopSpeed tables to typed records.</summary>
public static class OrisReader
{
    public const string JournalFile = "WIRING.TPS";
    public const string AccountNamesFile = "Acc_name.tps";
    public const string RatesFile = "Rate.tps";

    // WIRING field mapping (ORIS → DataHub). RANGE as entry number and REAL_DATE as posting date
    // follow the existing HIRO_WIRING table; confirm with the ORIS reference exports.
    public static IEnumerable<OrisJournalLine> ReadJournalLines(Stream stream, string fileName = JournalFile)
    {
        foreach (var r in TpsTableReader.ReadRecords(stream, fileName))
        {
            var debitRaw = r.GetString("DEBET");
            var creditRaw = r.GetString("KREDIT");
            OrisAccount.TryParse(debitRaw, out var debit);
            OrisAccount.TryParse(creditRaw, out var credit);

            yield return new OrisJournalLine(
                RecordNumber: r.RecordNumber,
                DocumentNumber: r.GetString("DOC"),
                EntryNumber: r.GetLong("RANGE"),
                Debit: debit,
                DebitRaw: debitRaw,
                Credit: credit,
                CreditRaw: creditRaw,
                Amount: r.GetDecimal("MONEY") ?? 0m,
                Currency: r.GetString("MON_TYPE"),
                Description: r.GetString("STORY"),
                Quantity: r.GetDecimal("QTY"),
                Unit: r.GetString("VELU"),
                PostedBy: r.GetString("USER_NAME"),
                OperationDate: r.GetDate("DATE"),
                PostingDate: r.GetDate("REAL_DATE"))
            {
                ExchangeRate = r.GetDecimal("CURS") is > 0 and var rate ? rate : null,
            };
        }
    }

    public static IEnumerable<OrisRate> ReadRates(Stream stream, string fileName = RatesFile)
    {
        foreach (var r in TpsTableReader.ReadRecords(stream, fileName))
        {
            var date = r.GetDate("DATE");
            var rate = r.GetDecimal("CURS") ?? 0;
            var quantity = r.GetDecimal("QTY") is > 0 and var q ? q : 1;
            if (date is { } d && rate > 0)
                yield return new OrisRate(r.GetString("MON_TYPE"), d, rate / quantity);
        }
    }

    public static IEnumerable<OrisAccountName> ReadAccountNames(Stream stream, string fileName = AccountNamesFile)
    {
        foreach (var r in TpsTableReader.ReadRecords(stream, fileName))
        {
            var raw = r.GetString("COUNT");
            OrisAccount.TryParse(raw, out var account);
            yield return new OrisAccountName(account, raw, (int)r.GetLong("LEVEL"), r.GetString("NAME"), r.GetString("MON_TYPE"));
        }
    }
}
