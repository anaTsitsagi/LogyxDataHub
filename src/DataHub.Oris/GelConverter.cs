namespace DataHub.Oris;

/// <summary>
/// Computes each journal line's GEL amount the way ORIS's own reports do.
/// </summary>
/// <remarks>
/// ORIS stores MONEY in the line's currency (MON_TYPE). For reports it uses the GEL equivalent:
/// MONEY × the rate recorded on the line (CURS), or, when none was recorded, the rate from the
/// daily rate table (Rate.tps) for the latest date on or before the operation date; rounded to
/// tetri, half away from zero. Lines without a currency are GEL exchange-difference postings.
/// Verified against ORIS's 2024 turnover register for HIRO: every account matches to the tetri.
/// </remarks>
public sealed class GelConverter
{
    public const string NationalCurrency = "GEL";

    private readonly Dictionary<string, (DateOnly[] Dates, decimal[] Rates)> _rates;

    public GelConverter(IEnumerable<OrisRate> rates) =>
        _rates = rates
            .GroupBy(r => r.Currency.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g =>
            {
                var ordered = g.GroupBy(r => r.Date).Select(d => d.Last()).OrderBy(r => r.Date).ToArray();
                return (ordered.Select(r => r.Date).ToArray(), ordered.Select(r => r.Rate).ToArray());
            });

    public static GelConverter WithoutRateTable { get; } = new([]);

    public static bool IsNational(string currency) =>
        currency.Length == 0 || currency.Equals(NationalCurrency, StringComparison.OrdinalIgnoreCase);

    /// <exception cref="MissingRateException">A foreign-currency line has no rate on the line and none in the table.</exception>
    public IEnumerable<OrisJournalLine> Apply(IEnumerable<OrisJournalLine> lines)
    {
        foreach (var line in lines)
        {
            if (IsNational(line.Currency))
            {
                yield return line with { Currency = NationalCurrency, AmountGel = line.Amount };
                continue;
            }

            var rate = line.ExchangeRate ?? RateOn(line.Currency, line.OperationDate)
                ?? throw new MissingRateException(line.Currency, line.OperationDate, line.RecordNumber);
            yield return line with
            {
                ExchangeRate = rate,
                AmountGel = Math.Round(line.Amount * rate, 2, MidpointRounding.AwayFromZero),
            };
        }
    }

    private decimal? RateOn(string currency, DateOnly? date)
    {
        if (date is not { } d || !_rates.TryGetValue(currency.Trim().ToUpperInvariant(), out var table)) return null;
        int i = Array.BinarySearch(table.Dates, d);
        if (i < 0) i = ~i - 1; // latest date before d
        return i >= 0 ? table.Rates[i] : null;
    }
}

public sealed class MissingRateException(string currency, DateOnly? date, int recordNumber)
    : Exception($"No {currency} exchange rate on or before {date?.ToString("yyyy-MM-dd") ?? "(no date)"} (WIRING record {recordNumber}).")
{
    public string Currency { get; } = currency;
}
