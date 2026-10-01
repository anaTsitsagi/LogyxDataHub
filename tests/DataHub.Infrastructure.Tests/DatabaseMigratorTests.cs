using DataHub.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataHub.Infrastructure.Tests;

public sealed class DatabaseMigratorTests
{
    [Fact]
    public async Task Creates_a_fresh_database_and_is_idempotent()
    {
        var server = Environment.GetEnvironmentVariable("DATAHUB_TEST_SQL")
            ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";
        var options = new DatabaseOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = $"DataHubMigratorTest_{Guid.NewGuid():N}" }.ConnectionString,
            CompatibilityLevel = 130,
        };
        var builder = new DbContextOptionsBuilder<DataHubDbContext>();
        PersistenceRegistration.Configure(builder, options);

        await using var db = new DataHubDbContext(builder.Options);
        try
        {
            var migrator = new DatabaseMigrator(db, NullLogger<DatabaseMigrator>.Instance);

            var applied = await migrator.MigrateAsync(default);
            Assert.Equal(db.Database.GetMigrations(), applied);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await db.Companies.CountAsync());

            Assert.Empty(await migrator.MigrateAsync(default));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
