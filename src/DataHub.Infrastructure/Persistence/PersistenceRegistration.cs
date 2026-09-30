using DataHub.Application;
using DataHub.Application.Datasets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace DataHub.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string ConnectionStringName = "DataHub";

    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// SQL Server compatibility level EF Core generates SQL for. 150 = SQL Server 2019.
    /// Lower it (e.g. 130 for 2016) if TBC's server is older.
    /// </summary>
    public int CompatibilityLevel { get; set; } = 150;
}

public static class PersistenceRegistration
{
    public static IServiceCollection AddDataHubPersistence(this IServiceCollection services, DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException($"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured.");

        services.AddDbContext<DataHubDbContext>(o => Configure(o, options));
        services.AddScoped<IDataHubDb>(sp => sp.GetRequiredService<DataHubDbContext>());
        services.AddScoped<IDatasetStore, SqlDatasetStore>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder builder, DatabaseOptions options) =>
        builder.UseSqlServer(options.ConnectionString, sql =>
        {
            sql.UseCompatibilityLevel(options.CompatibilityLevel);
            sql.MigrationsHistoryTable("__EFMigrationsHistory", DataHubDbContext.Schema);
            sql.EnableRetryOnFailure();
        });
}

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DataHubDbContext>
{
    public DataHubDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<DataHubDbContext>();
        PersistenceRegistration.Configure(builder, new DatabaseOptions { ConnectionString = "Server=design-time;Database=DataHub" });
        return new DataHubDbContext(builder.Options);
    }
}
