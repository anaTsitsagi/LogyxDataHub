using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataHub.Domain;
using DataHub.Infrastructure.Persistence;
using DataHub.Infrastructure.Tests;
using DataHub.Oris;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataHub.Api.Tests;

/// <summary>/reports/* over HTTP: contract shape, tenant enforcement, filters and paging.</summary>
[Collection(SqlCollection.Name)]
public sealed class ReportsApiTests(SqlDatabaseFixture db) : IDisposable
{
    private readonly ApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private static OrisJournalLine Line(DateOnly date, string debit, string credit, decimal amount, string currency = "GEL") =>
        new(1, "DOC-1", 1, OrisAccount.Parse(debit), debit, OrisAccount.Parse(credit), credit, amount, currency,
            "ოპერაცია", null, "", "ოპერატორი", date, date);

    /// <summary>
    /// A company with: opening capital 1000 (2023), a 100 GEL sale, a 50 USD sale at rate 2.70 (= 135 GEL)
    /// and a 16.87 GEL expense. Returns its tenant id.
    /// </summary>
    private Task<Guid> CompanyWithoutDataAsync() => CompanyAsync(null);

    private Task<Guid> CompanyWithDataAsync(params OrisJournalLine[] lines) => CompanyAsync(lines.Length > 0 ? lines :
    [
        Line(new(2023, 12, 31), "1 2 10", "5 1 50", 1000m),
        Line(new(2024, 1, 15), "1 2 10", "6 1 10", 100m),
        Line(new(2024, 1, 31), "1 2 20 2", "6 1 10", 50m, "USD"),
        Line(new(2024, 2, 1), "7 4 10", "1 2 10", 16.87m),
    ]);

    private async Task<Guid> CompanyAsync(OrisJournalLine[]? lines)
    {
        await using var context = db.CreateContext();
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "შპს ტესტი", CreatedAt = DateTimeOffset.UtcNow,
            CompanyCode = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(),
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        if (lines is null) return company.TenantId;

        var converter = new GelConverter([new OrisRate("USD", new DateOnly(2024, 1, 1), 2.70m)]);
        var store = new SqlDatasetStore(context, NullLogger<SqlDatasetStore>.Instance, TimeProvider.System);
        var dataset = await store.CreateStagingAsync(company.Id, Guid.NewGuid(), default);
        await store.WriteJournalAsync(dataset, converter.Apply(lines), default);
        await store.ActivateAsync(dataset, default);
        return company.TenantId;
    }

    private async Task<HttpClient> ClientAsync(Guid? tenant, string scope = "datahub.reports")
    {
        var http = _factory.CreateClient();
        var token = await (await http.PostAsJsonAsync("/dev/token", new { clientId = "tbc-risk", scope })).Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (tenant is { } t) http.DefaultRequestHeaders.Add("X-Tenant-Id", t.ToString());
        return http;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    [Fact]
    public async Task Reports_require_a_token_with_the_reports_scope()
    {
        var anonymous = await _factory.CreateClient().GetAsync("/reports/balance-sheet?toDate=2024-12-31");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var invitationsOnly = await ClientAsync(Guid.NewGuid(), scope: "datahub.invitations");
        Assert.Equal(HttpStatusCode.Forbidden, (await invitationsOnly.GetAsync("/reports/balance-sheet?toDate=2024-12-31")).StatusCode);
    }

    [Theory]
    [InlineData(null, "TENANT_REQUIRED", HttpStatusCode.BadRequest)]
    [InlineData("company-42", "TENANT_INVALID", HttpStatusCode.BadRequest)]
    [InlineData("8f7a61a4-9c55-4d6e-9d2e-3f0f8c7e1a11", "TENANT_NOT_FOUND", HttpStatusCode.NotFound)]
    public async Task Tenant_header_is_required_and_must_be_known(string? tenant, string code, HttpStatusCode status)
    {
        var http = await ClientAsync(null);
        if (tenant is not null) http.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);

        var response = await http.GetAsync("/reports/journal-entries?fromDate=2024-01-01&toDate=2024-12-31");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Company_without_processed_data_gets_a_clear_404()
    {
        var http = await ClientAsync(await CompanyWithoutDataAsync());
        var response = await http.GetAsync("/reports/balance-sheet?toDate=2024-12-31");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("DATA_NOT_AVAILABLE", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Tenant_id_query_parameter_must_match_the_header()
    {
        var http = await ClientAsync(await CompanyWithDataAsync());
        var response = await http.GetAsync($"/reports/journal-entries?fromDate=2024-01-01&toDate=2024-12-31&tenantId={Guid.NewGuid()}");

        Assert.Equal("TENANT_MISMATCH", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Each_tenant_sees_only_its_own_data()
    {
        var mine = await CompanyWithDataAsync();
        await CompanyWithDataAsync(Line(new(2024, 1, 10), "1 2 10", "6 1 10", 999m));

        var items = await (await ClientAsync(mine)).GetFromJsonAsync<JsonElement>("/reports/journal-entries?fromDate=2024-01-01&toDate=2024-12-31");

        Assert.Equal([100m, 50m, 16.87m], items.EnumerateArray().Select(i => i.GetProperty("amount").GetDecimal()));
    }

    [Fact]
    public async Task Journal_entries_use_contract_fields_inclusive_dates_and_paging_headers()
    {
        var http = await ClientAsync(await CompanyWithDataAsync());

        var response = await http.GetAsync("/reports/journal-entries?fromDate=2024-01-15&toDate=2024-02-01&page=2&pageSize=2");
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(("3", "2", "2", "2"), (Header(response, "X-Total-Count"), Header(response, "X-Page"),
            Header(response, "X-Page-Size"), Header(response, "X-Total-Pages")));
        var line = Assert.Single(items.EnumerateArray());
        Assert.Equal("7410", line.GetProperty("debet").GetString());
        Assert.Equal("1210", line.GetProperty("credit").GetString());
        Assert.Equal("", line.GetProperty("creditSub").GetString());
        Assert.Equal("16.87", line.GetProperty("amount").GetRawText());
        Assert.Equal("2024-02-01", line.GetProperty("operationDate").GetString());
        Assert.Equal("ოპერაცია", line.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Journal_filters_by_account_in_oris_form_and_by_currency()
    {
        var http = await ClientAsync(await CompanyWithDataAsync());

        var bank = await http.GetFromJsonAsync<JsonElement>("/reports/journal-entries?fromDate=2024-01-01&toDate=2024-12-31&accountNumber=1%202%2010");
        Assert.Equal([100m, 16.87m], bank.EnumerateArray().Select(i => i.GetProperty("amount").GetDecimal()));

        var usd = await http.GetFromJsonAsync<JsonElement>("/reports/journal-entries?fromDate=2024-01-01&toDate=2024-12-31&currency=USD");
        var line = Assert.Single(usd.EnumerateArray());
        Assert.Equal(("1220", "2", 50m, 135m), (line.GetProperty("debet").GetString(), line.GetProperty("debetSub").GetString(),
            line.GetProperty("amount").GetDecimal(), line.GetProperty("amountGel").GetDecimal()));

        var group = await http.GetFromJsonAsync<JsonElement>("/reports/journal-entries?fromDate=2024-01-01&toDate=2024-12-31&accountNumber=1200,7410");
        Assert.Equal(3, group.GetArrayLength());
    }

    [Theory]
    [InlineData("fromDate=2024-02-01&toDate=2024-01-01", "DATE_RANGE_INVALID")]
    [InlineData("toDate=2024-01-01", "DATE_REQUIRED")]
    [InlineData("fromDate=2024-01-01&toDate=2024-12-31&pageSize=501", "PAGE_SIZE_INVALID")]
    [InlineData("fromDate=2024-01-01&toDate=2024-12-31&accountNumber=12x", "ACCOUNT_INVALID")]
    public async Task Invalid_parameters_are_rejected_with_a_code(string query, string code)
    {
        var http = await ClientAsync(await CompanyWithDataAsync());
        var response = await http.GetAsync($"/reports/journal-entries?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Turnover_register_has_the_contract_shape_in_gel()
    {
        var http = await ClientAsync(await CompanyWithDataAsync());
        var register = await http.GetFromJsonAsync<JsonElement>("/reports/turnover-register?fromDate=2024-01-01&toDate=2024-02-01");

        Assert.Equal(("GEL", "2024-01-01", "2024-02-01"), (register.GetProperty("currency").GetString(),
            register.GetProperty("startDate").GetString(), register.GetProperty("endDate").GetString()));
        var rows = register.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("account").GetString()!);
        Assert.Equal(["1000", "1200", "1210", "1220", "1220 2", "5000", "5100", "5150", "6000", "6100", "6110", "7000", "7400", "7410"], rows.Keys);

        var bank = rows["1210"];
        Assert.Equal("1000.00", bank.GetProperty("opening").GetProperty("debit").GetString());
        Assert.Equal(JsonValueKind.Null, bank.GetProperty("opening").GetProperty("credit").ValueKind);
        Assert.Equal(("100.00", "16.87"), (bank.GetProperty("turnover").GetProperty("debit").GetString(), bank.GetProperty("turnover").GetProperty("credit").GetString()));
        Assert.Equal("1083.13", bank.GetProperty("closing").GetProperty("debit").GetString());
        Assert.Equal("135.00", rows["1220 2"].GetProperty("turnover").GetProperty("debit").GetString()); // 50 USD × 2.70

        var total = register.GetProperty("total");
        Assert.Equal(("1000.00", "1000.00"), (total.GetProperty("opening").GetProperty("debit").GetString(), total.GetProperty("opening").GetProperty("credit").GetString()));
        Assert.Equal(("251.87", "251.87"), (total.GetProperty("turnover").GetProperty("debit").GetString(), total.GetProperty("turnover").GetProperty("credit").GetString()));
    }

    [Fact]
    public async Task Balance_sheet_has_the_contract_shape_and_balances()
    {
        var http = await ClientAsync(await CompanyWithDataAsync());
        var sheet = await http.GetFromJsonAsync<JsonElement>("/reports/balance-sheet?toDate=2024-02-01");

        Assert.Equal(("Balance Sheet", "2024-02-01", "GEL"), (sheet.GetProperty("title").GetString(),
            sheet.GetProperty("asOf").GetString(), sheet.GetProperty("currency").GetString()));
        var current = sheet.GetProperty("assets").GetProperty("currentAssets");
        Assert.Equal("1218.13", current.GetProperty("bankAccounts").GetString()); // 1210 1083.13 + 1220 135.00
        Assert.Equal(JsonValueKind.Null, current.GetProperty("cash").ValueKind);
        var equity = sheet.GetProperty("equity").GetProperty("equityComponents");
        Assert.Equal("1000.00", equity.GetProperty("shareholdersEquity").GetString());
        Assert.Equal("218.13", equity.GetProperty("profitLossForPeriod").GetString()); // 235.00 income − 16.87 expense
        Assert.Equal("1218.13", sheet.GetProperty("assets").GetProperty("totals").GetProperty("totalAssets").GetString());
        Assert.Equal("1218.13", sheet.GetProperty("totals").GetProperty("totalLiabilitiesAndEquity").GetString());
    }

    [Fact]
    public async Task Readiness_checks_the_database()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;
}
