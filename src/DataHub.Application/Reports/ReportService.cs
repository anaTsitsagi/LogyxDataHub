using DataHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Reports;

/// <summary>
/// Reports for TBC systems, always computed from the company's active dataset only
/// (the data of the last successfully processed upload).
/// </summary>
/// <remarks>
/// Turnover and balances are in GEL: each line's GEL equivalent, computed at import exactly as ORIS
/// does (see <see cref="Oris.GelConverter"/>). The journal shows each line in its own currency plus the
/// GEL amount. The currency filter selects lines by their own currency. Dates are operation dates
/// (ORIS DATE) and inclusive.
/// </remarks>
public sealed class ReportService(IDataHubDb db, IOptions<BalanceSheetOptions> balanceSheet, ILogger<ReportService> logger)
{
    public const int MaxPageSize = 500;

    public async Task<Page<JournalEntryItem>> JournalEntriesAsync(ReportQuery query, CancellationToken ct)
    {
        Validate(query, requireRange: true);
        var datasetId = await ActiveDatasetAsync(query.TenantId, ct);

        var lines = Lines(datasetId, query)
            .Where(j => j.OperationDate >= query.FromDate && j.OperationDate <= query.ToDate);
        if (query.AccountNumbers.Count > 0)
            lines = lines.Where(AccountFilter.AnySide(ParseAccounts(query)));

        int total = await lines.CountAsync(ct);
        var items = await lines
            .OrderBy(j => j.OperationDate).ThenBy(j => j.EntryNumber).ThenBy(j => j.Id) // stable across pages
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(j => new JournalEntryItem(j.DocumentNumber, j.EntryNumber, j.Debet, j.DebetSub, j.Credit, j.CreditSub,
                j.Amount, j.Currency, j.AmountGel, j.Description, j.Quantity, j.Unit, j.PostedBy, j.OperationDate, j.PostingDate))
            .ToListAsync(ct);

        // decimal(19,4) columns come back as e.g. 16.8700; serialize them as 16.87.
        items = [.. items.Select(i => i with
        {
            Amount = Trim(i.Amount), AmountGel = Trim(i.AmountGel), Quantity = i.Quantity is { } q ? Trim(q) : null,
            Debet = i.Debet?.Trim(), Credit = i.Credit?.Trim(),
        })];
        return new Page<JournalEntryItem>(items, query.Page, query.PageSize, total);
    }

    /// <summary>
    /// Opening balance, turnover and closing balance per account for the period, rolled up to
    /// groups (XY00) and classes (X000) and broken down by sub-account, as ORIS's turnover register.
    /// </summary>
    /// <returns>The page of rows, the total row count (for paging) and the register with totals of all selected rows.</returns>
    public async Task<(TurnoverRegister Register, int TotalCount)> TurnoverRegisterAsync(ReportQuery query, CancellationToken ct)
    {
        Validate(query, requireRange: true);
        var datasetId = await ActiveDatasetAsync(query.TenantId, ct);
        var filters = ParseAccounts(query);

        var rows = new SortedDictionary<string, Row>(StringComparer.Ordinal);
        foreach (var side in await SideTotalsAsync(datasetId, query, query.FromDate, query.ToDate, ct))
        {
            foreach (var key in Ancestors(side.Code, side.Sub))
            {
                if (!rows.TryGetValue(key.Key, out var row)) rows[key.Key] = row = new Row(key.Key, key.Code, key.Sub, key.Level);
                row.Add(side);
            }
        }

        // Rows the caller asked for: every account, or the requested accounts and everything under them.
        var selected = rows.Values
            .Where(r => r.HasActivity && (filters.Count == 0 || filters.Any(f => f.Matches(r.Code, r.Sub) && IsWithin(r, f))))
            .ToList();
        // Totals add up the top-most selected rows, so nested rows are not counted twice.
        var roots = filters.Count == 0
            ? selected.Where(r => r.Level == 1).ToList()
            : selected.Where(r => filters.Any(f => r.Key == f.Key)).ToList();

        var names = await NamesAsync(datasetId, selected, ct);
        var page = selected.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(r => r.ToItem(names.GetValueOrDefault(r.Key) ?? (r.Level <= 2 ? StandardChart.NameOf(r.Key) : null)))
            .ToList();

        var total = new TurnoverTotal(
            new DebitCredit(Money.Format(roots.Sum(r => Math.Max(r.Opening, 0))), Money.Format(roots.Sum(r => Math.Max(-r.Opening, 0)))),
            DebitCredit.Turnover(roots.Sum(r => r.Debit), roots.Sum(r => r.Credit)),
            new DebitCredit(Money.Format(roots.Sum(r => Math.Max(r.Closing, 0))), Money.Format(roots.Sum(r => Math.Max(-r.Closing, 0)))));

        return (new TurnoverRegister("ბრუნვითი უწყისი", query.FromDate, query.ToDate, Oris.GelConverter.NationalCurrency, page, total), selected.Count);
    }

    /// <summary>Balance sheet as of <paramref name="asOf"/> (inclusive), from account balances mapped to lines.</summary>
    public async Task<BalanceSheet> BalanceSheetAsync(Guid tenantId, DateOnly asOf, IReadOnlyList<string> currencies, CancellationToken ct)
    {
        var query = new ReportQuery(tenantId, DateOnly.MinValue, asOf, [], currencies);
        var datasetId = await ActiveDatasetAsync(tenantId, ct);

        var balances = new Dictionary<string, decimal>();
        foreach (var side in await SideTotalsAsync(datasetId, query, from: null, asOf, ct))
            balances[side.Code] = balances.GetValueOrDefault(side.Code) + side.PeriodDebit - side.PeriodCredit;

        var index = balanceSheet.Value.PrefixIndex();
        var lines = new Dictionary<string, decimal?>();
        foreach (var (code, debitBalance) in balances)
        {
            var line = BalanceSheetOptions.LineFor(code, index);
            if (line is null)
            {
                logger.LogWarning("Account {Account} is not mapped to any balance sheet line; balance {Balance} left out", code, debitBalance);
                continue;
            }
            decimal amount = line.StartsWith("assets.", StringComparison.Ordinal) ? debitBalance : -debitBalance;
            lines[line] = (lines.GetValueOrDefault(line) ?? 0) + amount;
        }

        return BalanceSheetBuilder.Build(asOf, Oris.GelConverter.NationalCurrency, lines);
    }

    private async Task<Guid> ActiveDatasetAsync(Guid tenantId, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.ActiveDatasetId })
            .SingleOrDefaultAsync(ct)
            ?? throw new DataHubException(ErrorKind.NotFound, "TENANT_NOT_FOUND", "No company has this X-Tenant-Id.");
        return company.ActiveDatasetId
            ?? throw new DataHubException(ErrorKind.NotFound, "DATA_NOT_AVAILABLE",
                "The company has not uploaded ORIS data yet, or its upload is still being processed.");
    }

    private IQueryable<JournalEntry> Lines(Guid datasetId, ReportQuery query)
    {
        var lines = db.JournalEntries.AsNoTracking().Where(j => j.DatasetId == datasetId && j.OperationDate != null);
        if (query.Currencies.Count > 0)
        {
            var currencies = query.Currencies.Select(c => c.Trim().ToUpperInvariant()).ToList();
            lines = lines.Where(j => currencies.Contains(j.Currency));
        }
        return lines;
    }

    private sealed record SideTotal(string Code, string Sub, decimal OpeningDebit, decimal OpeningCredit, decimal PeriodDebit, decimal PeriodCredit);

    /// <summary>
    /// Debit and credit sums per posting account (code + sub-account), split into before-the-period
    /// and within-the-period, computed in SQL. Without <paramref name="from"/> everything up to <paramref name="to"/> is "period".
    /// </summary>
    private async Task<List<SideTotal>> SideTotalsAsync(Guid datasetId, ReportQuery query, DateOnly? from, DateOnly to, CancellationToken ct)
    {
        var start = from ?? DateOnly.MinValue;
        var lines = Lines(datasetId, query).Where(j => j.OperationDate <= to);

        var debits = await lines.Where(j => j.Debet != null)
            .GroupBy(j => new { j.Debet, j.DebetSub })
            .Select(g => new
            {
                g.Key.Debet, g.Key.DebetSub,
                Before = g.Sum(j => j.OperationDate < start ? j.AmountGel : 0m),
                In = g.Sum(j => j.OperationDate >= start ? j.AmountGel : 0m),
            })
            .ToListAsync(ct);
        var credits = await lines.Where(j => j.Credit != null)
            .GroupBy(j => new { j.Credit, j.CreditSub })
            .Select(g => new
            {
                g.Key.Credit, g.Key.CreditSub,
                Before = g.Sum(j => j.OperationDate < start ? j.AmountGel : 0m),
                In = g.Sum(j => j.OperationDate >= start ? j.AmountGel : 0m),
            })
            .ToListAsync(ct);

        return
        [
            .. debits.Select(d => new SideTotal(d.Debet!.Trim(), d.DebetSub, d.Before, 0, d.In, 0)),
            .. credits.Select(c => new SideTotal(c.Credit!.Trim(), c.CreditSub, 0, c.Before, 0, c.In)),
        ];
    }

    private sealed record RowKey(string Key, string Code, string Sub, int Level);

    /// <summary>Class, group, account and each sub-account level a posting belongs to (1000 → 1400 → 1410 → 1410 1 → 1410 1 267).</summary>
    private static IEnumerable<RowKey> Ancestors(string code, string sub)
    {
        yield return new RowKey($"{code[0]}000", $"{code[0]}000", "", 1);
        if (code[1] != '0') yield return new RowKey($"{code[..2]}00", $"{code[..2]}00", "", 2);
        if (code[2..] != "00") yield return new RowKey(code, code, "", 3);

        var parts = sub.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i <= parts.Length; i++)
        {
            var path = string.Join(' ', parts[..i]);
            yield return new RowKey($"{code} {path}", code, path, 3 + i);
        }
    }

    /// <summary>
    /// Excludes summary rows above the filtered account: group 1400 shares the prefix "14" with
    /// filter 1410 but is not part of it. A row covers 1 (class), 2 (group) or 4 (account) code digits.
    /// </summary>
    private static bool IsWithin(Row row, AccountFilter filter) =>
        (row.Level switch { 1 => 1, 2 => 2, _ => 4 }) >= filter.CodePrefix.Length;

    private sealed class Row(string key, string code, string sub, int level)
    {
        public string Key { get; } = key;
        public string Code { get; } = code;
        public string Sub { get; } = sub;
        public int Level { get; } = level;
        public decimal Opening { get; private set; }
        public decimal Debit { get; private set; }
        public decimal Credit { get; private set; }
        public decimal Closing => Opening + Debit - Credit;
        public bool HasActivity => Opening != 0 || Debit != 0 || Credit != 0;

        public void Add(SideTotal s)
        {
            Opening += s.OpeningDebit - s.OpeningCredit;
            Debit += s.PeriodDebit;
            Credit += s.PeriodCredit;
        }

        public TurnoverItem ToItem(string? name) =>
            new(Key, name, Level, DebitCredit.Balance(Opening), DebitCredit.Turnover(Debit, Credit), DebitCredit.Balance(Closing));
    }

    /// <summary>Account and sub-account names from the dataset's chart of accounts (Acc_name.tps).</summary>
    private async Task<Dictionary<string, string>> NamesAsync(Guid datasetId, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        var codes = rows.Select(r => r.Code).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.DatasetId == datasetId && codes.Contains(a.Code))
            .Select(a => new { a.Code, a.Sub, a.Name })
            .ToListAsync(ct);

        var names = new Dictionary<string, string>();
        foreach (var a in accounts)
            names.TryAdd(a.Sub.Length == 0 ? a.Code.Trim() : $"{a.Code.Trim()} {a.Sub}", a.Name);
        return names;
    }

    private static List<AccountFilter> ParseAccounts(ReportQuery query) => [.. query.AccountNumbers.Select(AccountFilter.Parse)];

    /// <summary>Drops trailing zeros (16.8700 → 16.87) without changing the value.</summary>
    private static decimal Trim(decimal value) => value / 1.000000000000000000000000000000000m;

    private static void Validate(ReportQuery query, bool requireRange)
    {
        if (requireRange && query.FromDate > query.ToDate)
            throw Invalid("DATE_RANGE_INVALID", "fromDate must not be after toDate.");
        if (query.Page < 1) throw Invalid("PAGE_INVALID", "page must be 1 or greater.");
        if (query.PageSize is < 1 or > MaxPageSize) throw Invalid("PAGE_SIZE_INVALID", $"pageSize must be between 1 and {MaxPageSize}.");
    }

    private static DataHubException Invalid(string code, string message) => new(ErrorKind.Validation, code, message);
}
