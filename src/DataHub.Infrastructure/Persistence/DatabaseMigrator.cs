using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataHub.Infrastructure.Persistence;

/// <summary>
/// Applies the EF Core migrations; run by DataHub.Migrator (a Helm pre-install/pre-upgrade Job), never by the apps.
/// Creates the database if it does not exist. EF Core holds a database lock while migrating, so two
/// concurrent runs cannot both apply the same migration.
/// </summary>
public sealed class DatabaseMigrator(DataHubDbContext db, ILogger<DatabaseMigrator> logger)
{
    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database is up to date; no migrations to apply");
            return pending;
        }

        logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, pending);
        await db.Database.MigrateAsync(ct);
        logger.LogInformation("Applied {Count} migration(s)", pending.Count);
        return pending;
    }
}
