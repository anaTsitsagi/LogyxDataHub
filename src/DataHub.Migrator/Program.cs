using DataHub.Hosting;
using DataHub.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Applies the database migrations and exits: 0 = up to date, 1 = failed (the Helm hook Job then fails the release).
// appsettings*.json are copied next to the binary, so they are found whatever the working directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.AddDataHubObservability("datahub-migrator");
builder.Services.AddDataHubPersistence(DatabaseOptions.From(builder.Configuration));

// Disposing the host flushes the logs to the OTLP receiver before the process exits.
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

try
{
    await using var scope = host.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancel.Token);
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Database migration failed");
    return 1;
}
