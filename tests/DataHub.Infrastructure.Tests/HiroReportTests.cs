using DataHub.Application.Reports;
using DataHub.Domain;
using DataHub.Infrastructure.Persistence;
using DataHub.Oris;
using DataHub.Oris.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataHub.Infrastructure.Tests;

/// <summary>
/// Reports over the real HIRO ORIS database, checked against the reports ORIS itself produced
/// ("TBC APIs Data Hub\2024 წლის ბრუნვითი უწყისი.XLSX" and the 1210/1410 journal extracts).
/// Skipped when the sample files are not present.
/// </summary>
[Collection(SqlCollection.Name)]
public sealed class HiroReportTests(SqlDatabaseFixture fixture)
{
    private const string Hiro = @"ORIS DB\ORIS 5\HIRO\";
    private static Guid? _tenant;
    private static readonly SemaphoreSlim Once = new(1, 1);

    private ReportService Reports(DataHubDbContext db) =>
        new(db, Options.Create(new BalanceSheetOptions()), NullLogger<ReportService>.Instance);

    private async Task<Guid> HiroTenantAsync()
    {
        await Once.WaitAsync();
        try
        {
            if (_tenant is { } t) return t;
            await using var db = fixture.CreateContext();
            var company = new Company
            {
                Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "შპს ჰირო", CreatedAt = DateTimeOffset.UtcNow,
                CompanyCode = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(),
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();

            var store = new SqlDatasetStore(db, NullLogger<SqlDatasetStore>.Instance, TimeProvider.System);
            var dataset = await store.CreateStagingAsync(company.Id, Guid.NewGuid(), default);
            await using (var s = File.OpenRead(Samples.PathOf(Hiro + "Acc_name.tps")))
                await store.WriteAccountsAsync(dataset, OrisReader.ReadAccountNames(s), default);
            GelConverter converter;
            await using (var s = File.OpenRead(Samples.PathOf(Hiro + "Rate.tps")))
                converter = new GelConverter(OrisReader.ReadRates(s).ToList());
            await using (var s = File.OpenRead(Samples.PathOf(Hiro + "WIRING.TPS")))
                await store.WriteJournalAsync(dataset, converter.Apply(OrisReader.ReadJournalLines(s)), default);
            await store.ActivateAsync(dataset, default);
            return (_tenant = company.TenantId).Value;
        }
        finally
        {
            Once.Release();
        }
    }

    private async Task<TurnoverRegister> Turnover2024Async(params string[] accounts)
    {
        await using var db = fixture.CreateContext();
        var query = new ReportQuery(await HiroTenantAsync(), new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), accounts, [], 1, 500);
        return (await Reports(db).TurnoverRegisterAsync(query, default)).Register;
    }

    private static (string? OD, string? OC, string? TD, string? TC, string? CD, string? CC) Figures(TurnoverItem i) =>
        (i.Opening.Debit, i.Opening.Credit, i.Turnover.Debit, i.Turnover.Credit, i.Closing.Debit, i.Closing.Credit);

    [SampleFact(Hiro + "WIRING.TPS")]
    public async Task Turnover_register_2024_matches_oris()
    {
        var register = await Turnover2024Async();
        var rows = register.Items.ToDictionary(i => i.Account);

        // Class, group, account and sub-account rows exactly as ORIS reports them.
        Assert.Equal(("15273.06", null, "446751.66", "446565.33", "15459.39", null), Figures(rows["1000"]));
        Assert.Equal(("951.56", null, "239598.37", "237814.14", "2735.79", null), Figures(rows["1200"]));
        Assert.Equal(("72.32", null, "5250.65", "5256.65", "66.32", null), Figures(rows["1220"]));
        Assert.Equal(("442.50", null, "672.60", "1115.10", null, null), Figures(rows["1410 1 267"]));
        Assert.Equal((null, "73179.08", "296669.68", "261450.60", null, "37960.00"), Figures(rows["3000"]));
        Assert.Equal((null, "2593.94", "197747.59", "160798.76", "34354.89", null), Figures(rows["3100"]));
        Assert.Equal(("103209.27", null, "0.00", "0.00", "103209.27", null), Figures(rows["5320"]));
        Assert.Equal((null, null, "0.00", "171741.77", null, "171741.77"), Figures(rows["6110"]));

        Assert.Equal(("73179.08", "73179.08"), (register.Total.Opening.Debit, register.Total.Opening.Credit));
        Assert.Equal(("880725.79", "880725.79"), (register.Total.Turnover.Debit, register.Total.Turnover.Credit));
        Assert.Equal(("209701.77", "209701.77"), (register.Total.Closing.Debit, register.Total.Closing.Credit));

        Assert.Equal("მიმდინარე აქტივები", rows["1000"].Name);
        Assert.Equal("ეროვნული ვალუტა რეზიდენტ ბანკში", rows["1210"].Name);
        Assert.Equal((1, 2, 3, 5), (rows["1000"].Level, rows["1200"].Level, rows["1220"].Level, rows["1410 1 267"].Level));
    }

    [SampleFact(Hiro + "WIRING.TPS")]
    public async Task Turnover_register_filtered_by_account_totals_that_account()
    {
        var register = await Turnover2024Async("1 4 10");

        Assert.All(register.Items, i => Assert.StartsWith("1410", i.Account));
        Assert.Equal("1410", register.Items[0].Account);
        Assert.Equal(("12856.50", "0.00"), (register.Total.Opening.Debit, register.Total.Opening.Credit));
        Assert.Equal(("202891.29", "205739.62"), (register.Total.Turnover.Debit, register.Total.Turnover.Credit));
    }

    // Both extracts are named "01.01.2024-01.03.2024", but the 1210 one ends on 29 February (its last
    // row, and the old app's "< 2024-03-01" query) while the 1410 one includes 1 March. Our toDate is
    // inclusive, as the API contract specifies, so each test uses the period its extract really covers.
    [SampleFact(Hiro + "WIRING.TPS")]
    public async Task Journal_for_account_1210_in_q1_matches_oris_extract()
    {
        await using var db = fixture.CreateContext();
        var query = new ReportQuery(await HiroTenantAsync(), new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 29), ["1210"], [], 1, 500);
        var page = await Reports(db).JournalEntriesAsync(query, default);

        Assert.Equal(29_871.11m, page.Items.Where(i => i.Debet == "1210").Sum(i => i.Amount));
        Assert.Equal(29_604.14m, page.Items.Where(i => i.Credit == "1210").Sum(i => i.Amount));
        Assert.Equal(page.TotalCount, page.Items.Count);
        Assert.True(page.Items.Zip(page.Items.Skip(1)).All(p => p.First.OperationDate <= p.Second.OperationDate));
    }

    [SampleFact(Hiro + "WIRING.TPS")]
    public async Task Journal_for_account_1410_in_q1_matches_oris_extract()
    {
        await using var db = fixture.CreateContext();
        var query = new ReportQuery(await HiroTenantAsync(), new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 1), ["1410"], [], 1, 20);
        var first = await Reports(db).JournalEntriesAsync(query, default);
        var all = await Reports(db).JournalEntriesAsync(query with { PageSize = 500 }, default);

        Assert.Equal(50, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(18_844.60m, all.Items.Where(i => i.Debet == "1410").Sum(i => i.Amount));
        Assert.Equal(19_812.20m, all.Items.Where(i => i.Credit == "1410").Sum(i => i.Amount));
    }

    [SampleFact(Hiro + "WIRING.TPS")]
    public async Task Balance_sheet_balances_and_matches_turnover_closing()
    {
        await using var db = fixture.CreateContext();
        var sheet = await Reports(db).BalanceSheetAsync(await HiroTenantAsync(), new DateOnly(2024, 12, 31), [], default);

        // Expected figures are the 2024 closing balances of ORIS's turnover register.
        var a = sheet.Assets;
        Assert.Equal("2735.79", a.CurrentAssets.BankAccounts);                  // 1200
        Assert.Equal("10008.17", a.CurrentAssets.TradeReceivables);             // 1410
        Assert.Equal("15459.39", a.Totals.TotalCurrentAssets);                  // class 1
        Assert.Equal(("2869.49", "-1674.14", "1195.35"),                        // 2160+2170, 2260+2270
            (a.FixedAssets.FurnitureOffice, a.FixedAssets.FurnitureAccumDep, a.FixedAssets.FurnitureTotal));
        Assert.Equal("1195.35", a.Totals.TotalFixedAssets);
        Assert.Equal("16654.74", a.Totals.TotalAssets);
        Assert.Null(a.FixedAssets.Land);                                        // no postings

        Assert.Equal("37960.00", sheet.Liabilities.Totals.TotalCurrentLiabilities); // class 3
        Assert.Equal("47302.15", sheet.Equity.EquityComponents.ShareholdersEquity); // 5150
        Assert.Equal("-103209.27", sheet.Equity.EquityComponents.RetainedEarnings); // 5320 (uncovered loss)
        Assert.Equal("34601.86", sheet.Equity.EquityComponents.ProfitLossForPeriod); // 6000 − 7000 − 8000
        Assert.Equal("16654.74", sheet.Totals.TotalLiabilitiesAndEquity);
    }
}
