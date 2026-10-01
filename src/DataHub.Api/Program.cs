using System.Text.Json;
using System.Text.Json.Serialization;
using DataHub.Api;
using DataHub.Api.Auth;
using DataHub.Api.Errors;
using DataHub.Application;
using DataHub.Hosting;
using DataHub.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.AddDataHubObservability("datahub-api");
builder.Services.AddDataHubApplication();
builder.Services.AddDataHubInfrastructure(builder.Configuration);
builder.Services.Configure<ApiAuthOptions>(builder.Configuration.GetSection(ApiAuthOptions.Section));
builder.Services.AddApiAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DataHubExceptionHandler>();
builder.Services.AddHealthChecks().AddDataHubReadiness();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Logyx DataHub API", Version = "v1" });
    if (builder.Environment.IsEnvironment(ApiAuthentication.LocalEnvironment))
        o.SwaggerDoc("dev", new OpenApiInfo { Title = "Logyx DataHub API (local dev tools)", Version = "dev" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token from TBC's identity provider (client credentials).",
    });
    o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] });
    o.DocInclusionPredicate((doc, api) => api.GroupName is null || api.GroupName == doc);
    o.SchemaFilter<OpenApiExamples>();
});

var app = builder.Build();

// Report calls name their tenant in X-Tenant-Id; add it to every log line of the request.
Func<HttpContext, Guid?> tenant = ctx =>
    Guid.TryParse(ctx.Request.Headers["X-Tenant-Id"], out var id) ? id : null;
app.UseDataHubRequestLogging(tenant);
app.UseTenantLogContext(tenant);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
    if (app.Environment.IsEnvironment(ApiAuthentication.LocalEnvironment))
        o.SwaggerEndpoint("/swagger/dev/swagger.json", "dev (local token)");
});
app.UseAuthentication();
app.UseAuthorization();

// Liveness runs no checks (the process answers); readiness also needs the database.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program;
