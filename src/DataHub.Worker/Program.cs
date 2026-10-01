using DataHub.Application;
using DataHub.Infrastructure;
using DataHub.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDataHubApplication();
builder.Services.AddDataHubInfrastructure(builder.Configuration);
builder.Services.AddHostedService<JobConsumer>();
builder.Services.AddHostedService<MaintenanceService>();
// Give an in-flight job time to hand itself back to the queue on shutdown (Kubernetes grace period must be longer).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

builder.Build().Run();
