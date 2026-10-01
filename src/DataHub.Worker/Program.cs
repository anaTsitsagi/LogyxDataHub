using DataHub.Application;
using DataHub.Hosting;
using DataHub.Infrastructure;
using DataHub.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// A web host only for the Kubernetes probes; the work itself runs in the hosted services.
var builder = WebApplication.CreateBuilder(args);

builder.AddDataHubObservability("datahub-worker");
builder.Services.AddDataHubApplication();
builder.Services.AddDataHubInfrastructure(builder.Configuration);
builder.Services.AddSingleton<WorkerHealth>();
builder.Services.AddHostedService<JobConsumer>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHealthChecks()
    .AddCheck<MaintenanceLoopCheck>("maintenance-loop", tags: ["live"])
    .AddCheck<ConsumerCheck>("rabbitmq-consumer", tags: ["ready"])
    .AddDataHubReadiness();
// Give an in-flight job time to hand itself back to the queue on shutdown (Kubernetes grace period must be longer).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();
