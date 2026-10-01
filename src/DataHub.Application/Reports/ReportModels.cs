using System.Globalization;
using System.Text.Json.Serialization;

namespace DataHub.Application.Reports;

/// <summary>Common report filters. Dates are inclusive on both ends and refer to the operation date (ORIS DATE).</summary>
public sealed record ReportQuery(
    Guid TenantId,
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<string> AccountNumbers,
    IReadOnlyList<string> Currencies,
    int Page = 1,
    int PageSize = 100);

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount)
{
    public int TotalPages => (TotalCount + PageSize - 1) / PageSize;
}

/// <summary>One journal line, with the field names of the API contract (logyx DataHub API.docx §3.4) plus <c>amountGel</c>.</summary>
public sealed record JournalEntryItem(
    string DocumentNumber,
    long EntryNumber,
    string? Debet,
    string DebetSub,
    string? Credit,
    string CreditSub,
    decimal Amount,
    string Currency,
    decimal AmountGel,
    string Description,
    decimal? Quantity,
    string Unit,
    string PostedBy,
    DateOnly? OperationDate,
    DateOnly? PostingDate);

/// <summary>A debit/credit pair. Amounts are strings with two decimals, as in the contract's JSON examples.</summary>
public sealed record DebitCredit(string? Debit, string? Credit)
{
    /// <summary>A balance: shown on the debit side when positive, on the credit side when negative (as ORIS does).</summary>
    public static DebitCredit Balance(decimal net) =>
        new(net > 0 ? Money.Format(net) : null, net < 0 ? Money.Format(-net) : null);

    public static DebitCredit Turnover(decimal debit, decimal credit) => new(Money.Format(debit), Money.Format(credit));
}

public sealed record TurnoverItem(
    string Account,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    int Level,
    DebitCredit Opening,
    DebitCredit Turnover,
    DebitCredit Closing);

public sealed record TurnoverTotal(DebitCredit Opening, DebitCredit Turnover, DebitCredit Closing);

public sealed record TurnoverRegister(
    string Title,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Currency,
    IReadOnlyList<TurnoverItem> Items,
    TurnoverTotal Total);

public static class Money
{
    public static string Format(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    public static string? Format(decimal? value) => value is { } v ? Format(v) : null;
}
