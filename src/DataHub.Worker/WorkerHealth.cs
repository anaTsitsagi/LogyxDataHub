using DataHub.Application.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DataHub.Worker;

/// <summary>What the worker's probes look at: the maintenance loop's last run and the consumer channel.</summary>
public sealed class WorkerHealth(TimeProvider clock)
{
    private long _lastMaintenance = clock.GetUtcNow().UtcTicks;

    public IChannel? ConsumerChannel { get; set; }

    public DateTimeOffset LastMaintenance => new(Interlocked.Read(ref _lastMaintenance), TimeSpan.Zero);

    public void MaintenanceRan() => Interlocked.Exchange(ref _lastMaintenance, clock.GetUtcNow().UtcTicks);
}

/// <summary>
/// Liveness: the maintenance loop keeps running even when the database or broker is down (it logs and
/// retries), so a loop that stops ticking means the process is stuck and should be restarted.
/// </summary>
public sealed class MaintenanceLoopCheck(WorkerHealth health, IOptions<ProcessingOptions> options, TimeProvider clock) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        var age = clock.GetUtcNow() - health.LastMaintenance;
        return Task.FromResult(age <= options.Value.LivenessTimeout
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Maintenance loop has not run for {age:hh\\:mm\\:ss}."));
    }
}

/// <summary>Readiness: the worker is consuming from RabbitMQ.</summary>
public sealed class ConsumerCheck(WorkerHealth health) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct) =>
        Task.FromResult(health.ConsumerChannel is { IsOpen: true }
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Not consuming from RabbitMQ."));
}
