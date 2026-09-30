using DataHub.Domain;
using DataHub.Infrastructure.Persistence;
using DataHub.Oris;
using DataHub.Oris.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Infrastructure.Tests;

[Collection(SqlCollection.Name)]
public class DatasetLifecycleTests(SqlDatabaseFixture fixture)
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    private SqlDatasetStore Store(DataHubDbContext db) => new(db, NullLogger<SqlDatasetStore>.Instance, _clock);

    private static OrisJournalLine Line(decimal amount, string debit = "1 2 10", string credit = "3 1 10 82") =>
        new(1, "DOC-1", 1, OrisAccount.Parse(debit), debit, OrisAccount.Parse(credit), credit, amount, "GEL",
            "შეძენილია საქონელი", 1m, "ცალი", "ნათია ხვედელიძე", new DateOnly(2024, 1, 15), new DateOnly(2024, 1, 16));

    private async Task<Guid> NewCompanyAsync()
    {
        await using var db = fixture.CreateContext();
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(),
            CompanyCode = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(),
            Name = "შპს ტესტი", CreatedAt = _clock.GetUtcNow(),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private async Task<Guid> ImportAsync(Guid companyId, params decimal[] amounts)
    {
        await using var db = fixture.CreateContext();
        var store = Store(db);
        var id = await store.CreateStagingAsync(companyId, Guid.NewGuid(), default);
        await store.WriteJournalAsync(id, amounts.Select(a => Line(a)), default);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return id;
    }

    private async Task<(Guid? Active, decimal Total)> ServedDataAsync(Guid companyId)
    {
        await using var db = fixture.CreateContext();
        var active = await db.Companies.Where(c => c.Id == companyId).Select(c => c.ActiveDatasetId).SingleAsync();
        var total = await db.JournalEntries.Where(j => j.DatasetId == active).SumAsync(j => j.Amount);
        return (active, total);
    }

    [Fact]
    public async Task Staging_data_is_not_served_until_activated()
    {
        var company = await NewCompanyAsync();
        await ImportAsync(company, 100m);

        Assert.Equal((null, 0m), await ServedDataAsync(company));
    }

    [Fact]
    public async Task Successful_processing_replaces_old_data_and_purge_removes_it()
    {
        var company = await NewCompanyAsync();
        var first = await ImportAsync(company, 100m, 50m);
        await using (var db = fixture.CreateContext()) Assert.True(await Store(db).ActivateAsync(first, default));

        var second = await ImportAsync(company, 999.99m);
        await using (var db = fixture.CreateContext()) Assert.True(await Store(db).ActivateAsync(second, default));

        Assert.Equal((second, 999.99m), await ServedDataAsync(company));

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(DatasetStatus.Superseded, (await db.Datasets.SingleAsync(d => d.Id == first)).Status);
            await Store(db).PurgeInactiveAsync(default);
            Assert.False(await db.JournalEntries.AnyAsync(j => j.DatasetId == first));
            Assert.False(await db.Datasets.AnyAsync(d => d.Id == first));
        }
    }

    [Fact]
    public async Task Failed_processing_leaves_existing_data_unchanged()
    {
        var company = await NewCompanyAsync();
        var good = await ImportAsync(company, 250m);
        await using (var db = fixture.CreateContext()) await Store(db).ActivateAsync(good, default);

        var broken = await ImportAsync(company, 1m, 2m, 3m);
        await using (var db = fixture.CreateContext())
        {
            await Store(db).MarkFailedAsync(broken, default);
            await Store(db).PurgeInactiveAsync(default);
        }

        Assert.Equal((good, 250m), await ServedDataAsync(company));
        await using (var db = fixture.CreateContext())
            Assert.False(await db.JournalEntries.AnyAsync(j => j.DatasetId == broken));
    }

    [Fact]
    public async Task Older_upload_finishing_late_does_not_overwrite_newer_data()
    {
        var company = await NewCompanyAsync();
        var older = await ImportAsync(company, 1m);
        var newer = await ImportAsync(company, 2m);

        await using (var db = fixture.CreateContext()) Assert.True(await Store(db).ActivateAsync(newer, default));
        await using (var db = fixture.CreateContext()) Assert.False(await Store(db).ActivateAsync(older, default));

        Assert.Equal((newer, 2m), await ServedDataAsync(company));
    }

    [Fact]
    public async Task Stores_georgian_text_accounts_and_dates_faithfully()
    {
        var company = await NewCompanyAsync();
        var id = await ImportAsync(company, 16.87m);

        await using var db = fixture.CreateContext();
        var row = await db.JournalEntries.SingleAsync(j => j.DatasetId == id);
        Assert.Equal("შეძენილია საქონელი", row.Description);
        Assert.Equal(("1210", "", "3110", "82"), (row.Debet, row.DebetSub, row.Credit, row.CreditSub));
        Assert.Equal(new DateOnly(2024, 1, 15), row.OperationDate);
        Assert.Equal(16.87m, row.Amount);
    }

    [SampleFact("WIRING.TPS")]
    public async Task Imports_reference_wiring_file_end_to_end()
    {
        var company = await NewCompanyAsync();
        await using var db = fixture.CreateContext();
        var store = Store(db);

        var id = await store.CreateStagingAsync(company, Guid.NewGuid(), default);
        await using (var stream = File.OpenRead(Samples.PathOf("WIRING.TPS")))
            Assert.Equal(16_781, await store.WriteJournalAsync(id, OrisReader.ReadJournalLines(stream), default));
        Assert.True(await store.ActivateAsync(id, default));

        Assert.Equal((id, 10_525_411.79m), await ServedDataAsync(company));
        Assert.Equal(16_781, (await db.Datasets.SingleAsync(d => d.Id == id)).JournalEntryCount);
    }
}
