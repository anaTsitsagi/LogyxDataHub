using DataHub.Application.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Worker.Tests;

public sealed class WorkerHealthTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IOptions<ProcessingOptions> _options = Options.Create(new ProcessingOptions { LivenessTimeout = TimeSpan.FromMinutes(15) });

    private static Task<HealthCheckResult> Check(IHealthCheck check) => check.CheckHealthAsync(new HealthCheckContext(), default);

    [Fact]
    public async Task Liveness_fails_only_when_the_maintenance_loop_stops()
    {
        var health = new WorkerHealth(_clock);
        var check = new MaintenanceLoopCheck(health, _options, _clock);
        Assert.Equal(HealthStatus.Healthy, (await Check(check)).Status); // just started

        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HealthStatus.Healthy, (await Check(check)).Status);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Unhealthy, (await Check(check)).Status);

        health.MaintenanceRan();
        Assert.Equal(HealthStatus.Healthy, (await Check(check)).Status);
    }

    [Fact]
    public async Task Readiness_needs_a_consumer_channel()
    {
        var health = new WorkerHealth(_clock);
        Assert.Equal(HealthStatus.Unhealthy, (await Check(new ConsumerCheck(health))).Status);
    }
}
