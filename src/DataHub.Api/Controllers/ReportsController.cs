using DataHub.Api.Auth;
using DataHub.Application;
using DataHub.Application.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataHub.Api.Controllers;

/// <param name="FromDate">Start date, inclusive (ISO 8601, e.g. 2024-01-01).</param>
/// <param name="ToDate">End date, inclusive.</param>
/// <param name="AccountNumber">Account filter, repeatable or comma-separated. ORIS form ("1 2 10", "1410 1 267") or compact
/// ("1210"). A class (1000) or group (1400) selects every account in it.</param>
/// <param name="Currency">Currency filter (GEL, USD, EUR), repeatable or comma-separated.</param>
/// <param name="TenantId">Company identifier; optional, must equal the X-Tenant-Id header when given.</param>
/// <param name="Page">Page number, from 1.</param>
/// <param name="PageSize">Rows per page, 1–500.</param>
public sealed record ReportParameters(
    DateOnly? FromDate,
    DateOnly? ToDate,
    string[]? AccountNumber,
    string[]? Currency,
    string? TenantId,
    int Page = 1,
    int PageSize = 100);

/// <summary>Financial reports for TBC LOS and Risk, per "logyx DataHub API.docx".</summary>
/// <remarks>
/// Every request must carry <c>X-Tenant-Id</c>: the tenant id returned when the invitation was created.
/// Reports always use the company's latest successfully processed ORIS upload. Paged endpoints return the
/// paging state in the <c>X-Total-Count</c>, <c>X-Page</c>, <c>X-Page-Size</c> and <c>X-Total-Pages</c> headers.
/// </remarks>
[ApiController]
[Route("reports")]
[Authorize(Policy = ApiAuthentication.ReportsPolicy)]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class ReportsController(ReportService reports) : ControllerBase
{
    /// <summary>Journal entries register (გატარებების რეესტრი).</summary>
    /// <remarks>
    /// Lines whose debit or credit account matches the filter, ordered by operation date. <c>amount</c> is in the
    /// line's <c>currency</c>; <c>amountGel</c> is its GEL equivalent as used by ORIS reports.
    /// </remarks>
    [HttpGet("journal-entries")]
    [ProducesResponseType<IReadOnlyList<JournalEntryItem>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<JournalEntryItem>> JournalEntries(
        [FromHeader(Name = "X-Tenant-Id")] string? tenantHeader, [FromQuery] ReportParameters parameters, CancellationToken ct)
    {
        var page = await reports.JournalEntriesAsync(Query(tenantHeader, parameters), ct);
        SetPaging(page.PageNumber, page.PageSize, page.TotalCount, page.TotalPages);
        return page.Items;
    }

    /// <summary>Turnover register (ბრუნვითი უწყისი) for the period, in GEL.</summary>
    /// <remarks>
    /// Opening balance, debit/credit turnover and closing balance per class (X000), group (XY00), account and
    /// sub-account, as ORIS reports them. A balance is shown on the debit side when positive and on the credit
    /// side when negative. <c>total</c> sums the classes, or the requested accounts when filtered, over all pages.
    /// </remarks>
    [HttpGet("turnover-register")]
    [ProducesResponseType<TurnoverRegister>(StatusCodes.Status200OK)]
    public async Task<TurnoverRegister> TurnoverRegister(
        [FromHeader(Name = "X-Tenant-Id")] string? tenantHeader, [FromQuery] ReportParameters parameters, CancellationToken ct)
    {
        var query = Query(tenantHeader, parameters);
        var (register, totalCount) = await reports.TurnoverRegisterAsync(query, ct);
        SetPaging(query.Page, query.PageSize, totalCount, (totalCount + query.PageSize - 1) / query.PageSize);
        return register;
    }

    /// <summary>Balance sheet (ბალანსი) as of a date, in GEL.</summary>
    /// <param name="tenantHeader">Company identifier.</param>
    /// <param name="toDate">Reporting date, inclusive.</param>
    /// <param name="currency">Optional: only lines in these currencies.</param>
    /// <param name="tenantId">Optional; must equal the X-Tenant-Id header when given.</param>
    /// <param name="ct">Cancellation.</param>
    [HttpGet("balance-sheet")]
    [ProducesResponseType<BalanceSheet>(StatusCodes.Status200OK)]
    public async Task<BalanceSheet> BalanceSheet(
        [FromHeader(Name = "X-Tenant-Id")] string? tenantHeader,
        [FromQuery] DateOnly? toDate,
        [FromQuery] string[]? currency,
        [FromQuery] string? tenantId,
        CancellationToken ct)
    {
        var tenant = Tenant(tenantHeader, tenantId);
        if (toDate is null) throw Invalid("DATE_REQUIRED", "toDate is required.");
        return await reports.BalanceSheetAsync(tenant, toDate.Value, Split(currency), ct);
    }

    private static ReportQuery Query(string? tenantHeader, ReportParameters p)
    {
        var tenant = Tenant(tenantHeader, p.TenantId);
        if (p.FromDate is null || p.ToDate is null) throw Invalid("DATE_REQUIRED", "fromDate and toDate are required.");
        return new ReportQuery(tenant, p.FromDate.Value, p.ToDate.Value, Split(p.AccountNumber), Split(p.Currency), p.Page, p.PageSize);
    }

    private static Guid Tenant(string? header, string? queryValue)
    {
        if (string.IsNullOrWhiteSpace(header)) throw Invalid("TENANT_REQUIRED", "The X-Tenant-Id header is required.");
        if (!Guid.TryParse(header, out var tenant)) throw Invalid("TENANT_INVALID", "X-Tenant-Id is not a valid tenant id.");
        if (!string.IsNullOrWhiteSpace(queryValue) && (!Guid.TryParse(queryValue, out var q) || q != tenant))
            throw Invalid("TENANT_MISMATCH", "The tenantId query parameter does not match the X-Tenant-Id header.");
        return tenant;
    }

    /// <summary>Accepts repeated parameters and comma-separated lists alike.</summary>
    private static string[] Split(string[]? values) =>
        values is null ? [] : [.. values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    private void SetPaging(int page, int pageSize, int totalCount, int totalPages)
    {
        var h = Response.Headers;
        h["X-Total-Count"] = totalCount.ToString();
        h["X-Page"] = page.ToString();
        h["X-Page-Size"] = pageSize.ToString();
        h["X-Total-Pages"] = totalPages.ToString();
    }

    private static DataHubException Invalid(string code, string message) => new(ErrorKind.Validation, code, message);
}
