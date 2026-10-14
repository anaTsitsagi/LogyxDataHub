using DataHub.Api.Staff;
using DataHub.Application;
using DataHub.Application.Invitations;
using DataHub.Application.Reports;
using DataHub.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DataHub.Api.Controllers;

/// <summary>
/// Bank staff portal for local testing: sends invitations and shows their progress and reports in a browser,
/// doing what TBC LOS does through the API. Local environment only (404 elsewhere), so it has no login.
/// </summary>
[Route("staff")]
[AllowAnonymous]
[LocalOnly]
[AutoValidateAntiforgeryToken]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class StaffController(
    InvitationService invitations,
    ReportService reports,
    IOptions<StaffPortalOptions> options,
    ILogger<StaffController> logger) : Controller
{
    /// <summary>Recorded as the invitation's API client, so staff invitations are told apart from LOS ones.</summary>
    public const string ClientId = "staff-portal";
    private const int RecentCount = 25;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new StaffIndexModel(new StaffInvitationForm(), await invitations.ListRecentAsync(RecentCount, ct), null, options.Value));

    [HttpPost("invitations")]
    public async Task<IActionResult> Create([FromForm] StaffInvitationForm form, CancellationToken ct)
    {
        var channels = (form.SendEmail ? NotificationChannels.Email : NotificationChannels.None)
                       | (form.SendSms ? NotificationChannels.Sms : NotificationChannels.None);
        try
        {
            var result = await invitations.CreateAsync(new CreateInvitationCommand(
                form.CompanyCode, form.CompanyName, form.Email, form.Phone, channels, form.RequestKey, ClientId), ct);
            return View("Details", await DetailsAsync(result.InvitationId, result, ct));
        }
        catch (DataHubException ex) when (ex.Kind == ErrorKind.Validation)
        {
            logger.LogInformation("Staff portal invitation rejected: {ErrorCode}", ex.Code);
            return View("Index", new StaffIndexModel(form, await invitations.ListRecentAsync(RecentCount, ct), ex.Message, options.Value));
        }
    }

    [HttpGet("invitations/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        try
        {
            return View(await DetailsAsync(id, null, ct));
        }
        catch (DataHubException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return NotFound();
        }
    }

    // The reports as JSON, for a browser tab: the same data LOS gets from GET /reports/*, without a token.

    [HttpGet("reports/{tenantId:guid}/journal-entries")]
    public async Task<IActionResult> JournalEntries(Guid tenantId, DateOnly? fromDate, DateOnly? toDate, string? accountNumber,
        string? currency, int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var result = await reports.JournalEntriesAsync(Query(tenantId, fromDate, toDate, accountNumber, currency, page, pageSize), ct);
        return Ok(new { result.PageNumber, result.PageSize, result.TotalCount, result.TotalPages, result.Items });
    }

    [HttpGet("reports/{tenantId:guid}/turnover-register")]
    public async Task<IActionResult> TurnoverRegister(Guid tenantId, DateOnly? fromDate, DateOnly? toDate, string? accountNumber,
        string? currency, int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var (register, totalCount) = await reports.TurnoverRegisterAsync(
            Query(tenantId, fromDate, toDate, accountNumber, currency, page, pageSize), ct);
        return Ok(new { page, pageSize, totalCount, register });
    }

    [HttpGet("reports/{tenantId:guid}/balance-sheet")]
    public async Task<IActionResult> BalanceSheet(Guid tenantId, DateOnly? toDate, string? currency, CancellationToken ct = default)
    {
        if (toDate is null) throw Invalid("DATE_REQUIRED", "toDate is required.");
        return Ok(await reports.BalanceSheetAsync(tenantId, toDate.Value, Split(currency), ct));
    }

    private async Task<StaffDetailsModel> DetailsAsync(Guid id, InvitationResult? justCreated, CancellationToken ct)
    {
        var summary = await invitations.GetSummaryAsync(id, ct);
        var period = await reports.DataPeriodAsync(summary.TenantId, ct);
        return new StaffDetailsModel(summary, invitations.LinkFor(id), options.Value, period, justCreated);
    }

    private static ReportQuery Query(Guid tenantId, DateOnly? fromDate, DateOnly? toDate, string? accountNumber, string? currency,
        int page, int pageSize)
    {
        if (fromDate is null || toDate is null) throw Invalid("DATE_REQUIRED", "fromDate and toDate are required.");
        return new ReportQuery(tenantId, fromDate.Value, toDate.Value, Split(accountNumber), Split(currency), page, pageSize);
    }

    private static string[] Split(string? value) =>
        value is null ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static DataHubException Invalid(string code, string message) => new(ErrorKind.Validation, code, message);
}
