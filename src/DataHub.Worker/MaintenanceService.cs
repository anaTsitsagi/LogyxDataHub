using DataHub.Application.Datasets;
using DataHub.Application.Processing;
using Microsoft.Extensions.Options;

namespace DataHub.Worker;

/// <summary>Runs the job recovery sweep and deletes rows of replaced or failed datasets.</summary>
public sealed class MaintenanceService(
    IServiceScopeFactory scopes,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var nextPurge = clock.GetUtcNow();
        using var timer = new PeriodicTimer(o.SweepInterval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<JobRecovery>().RunAsync(stoppingToken);
                if (result != new RecoveryResult(0, 0, 0))
                    logger.LogInformation("Recovery sweep: {Requeued} requeued, {Republished} republished, {Failed} failed",
                        result.Requeued, result.Republished, result.Failed);

                if (clock.GetUtcNow() >= nextPurge)
                {
                    long deleted = await scope.ServiceProvider.GetRequiredService<IDatasetStore>().PurgeInactiveAsync(stoppingToken);
                    if (deleted > 0) logger.LogInformation("Purged {Rows} rows of inactive datasets", deleted);
                    nextPurge = clock.GetUtcNow() + o.PurgeInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Maintenance run failed; retrying at the next interval");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
