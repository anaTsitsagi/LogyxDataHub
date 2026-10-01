using DataHub.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Infrastructure.Tests;

/// <summary>
/// Creates a throwaway database with all migrations applied and drops it afterwards.
/// Uses DATAHUB_TEST_SQL (a server connection string without a database) or LocalDB.
/// </summary>
public sealed class SqlDatabaseFixture : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("DATAHUB_TEST_SQL")
        ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    private readonly string _databaseName = $"DataHubTest_{Guid.NewGuid():N}";

    public DatabaseOptions Options { get; private set; } = null!;

    public DataHubDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<DataHubDbContext>();
        PersistenceRegistration.Configure(builder, Options);
        return new DataHubDbContext(builder.Options);
    }

    public async Task InitializeAsync()
    {
        var connection = new SqlConnectionStringBuilder(Server) { InitialCatalog = _databaseName };
        // LocalDB is SQL Server 2016 (compatibility level 130).
        Options = new DatabaseOptions { ConnectionString = connection.ConnectionString, CompatibilityLevel = 130 };
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlDatabaseFixture>
{
    public const string Name = "sql";
}
