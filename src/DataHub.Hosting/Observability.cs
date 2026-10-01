using System.Diagnostics;
using System.Reflection;
using DataHub.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace DataHub.Hosting;

/// <summary>OTLP/HTTP export settings (TBC requirement: central logging over OTLP/HTTP).</summary>
public sealed class OtlpOptions
{
    public const string Section = "Otlp";

    /// <summary>
    /// Base address of the OTLP/HTTP receiver, e.g. <c>http://seq:5341/ingest/otlp</c>. The signal paths
    /// (<c>/v1/logs</c>, <c>/v1/traces</c>, <c>/v1/metrics</c>) are appended. Empty = logs go to the console only.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Extra request headers as <c>key1=value1,key2=value2</c> (e.g. an API key). Comes from a Secret.</summary>
    public string Headers { get; set; } = "";

    /// <summary>Some receivers (Seq) accept logs and traces but not metrics.</summary>
    public bool ExportMetrics { get; set; } = true;

    internal Dictionary<string, string> ParseHeaders() =>
        Headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(kv => kv.Length == 2 && kv[0].Length > 0)
            .ToDictionary(kv => kv[0], kv => kv[1]);

    internal Uri SignalUri(string signal) => new($"{Endpoint.TrimEnd('/')}/v1/{signal}");
}

public static class Observability
{
    /// <summary>
    /// Serilog (console + OTLP/HTTP logs) and OpenTelemetry traces and metrics (OTLP/HTTP), shared by every host.
    /// Log levels come from the <c>Serilog:MinimumLevel</c> section.
    /// </summary>
    public static IHostApplicationBuilder AddDataHubObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        var otlp = builder.Configuration.GetSection(OtlpOptions.Section).Get<OtlpOptions>() ?? new OtlpOptions();
        string version = typeof(Observability).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        // In Kubernetes the machine name is the pod name.
        string instance = Environment.MachineName;
        string environment = builder.Environment.EnvironmentName;
        bool exportOtlp = !string.IsNullOrWhiteSpace(otlp.Endpoint);
        var headers = otlp.ParseHeaders();

        // Serilog writes the console itself; other providers (e.g. a test's log capture) still receive every event.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(writeToProviders: true, configureLogger: (services, log) =>
        {
            log.ReadFrom.Configuration(services.GetRequiredService<IConfiguration>())
                .Enrich.FromLogContext();

            // The official .NET images set DOTNET_RUNNING_IN_CONTAINER: write JSON to stdout there, readable text locally.
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
                log.WriteTo.Console(new RenderedCompactJsonFormatter());
            else
                log.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

            if (exportOtlp)
                log.WriteTo.OpenTelemetry(o =>
                {
                    o.Endpoint = otlp.SignalUri("logs").ToString();
                    o.Protocol = OtlpProtocol.HttpProtobuf;
                    o.Headers = headers;
                    o.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName,
                        ["service.version"] = version,
                        ["service.instance.id"] = instance,
                        ["deployment.environment.name"] = environment,
                    };
                });
        });

        if (!exportOtlp) return builder;

        void Exporter(OtlpExporterOptions o, string signal)
        {
            o.Endpoint = otlp.SignalUri(signal);
            o.Protocol = OtlpExportProtocol.HttpProtobuf;
            o.Headers = otlp.Headers;
        }

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: version, serviceInstanceId: instance)
                .AddAttributes([new("deployment.environment.name", environment)]))
            .WithTracing(t => t
                .AddSource(DataHubTelemetry.Name, "RabbitMQ.Client.*")
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                    // The raw path can carry the invitation link token; the route template (http.route) stays.
                    o.EnrichWithHttpRequest = (activity, request) =>
                    {
                        string path = SensitiveData.RedactPath(request.Path);
                        if (path != request.Path.Value)
                        {
                            activity.SetTag("url.path", path);
                            activity.SetTag("url.full", null);
                        }
                    };
                })
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation()
                .AddAWSInstrumentation()
                .AddOtlpExporter(o => Exporter(o, "traces")));

        if (otlp.ExportMetrics)
            telemetry.WithMetrics(m => m
                .AddMeter(DataHubTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(o => Exporter(o, "metrics")));

        return builder;
    }

    /// <summary>
    /// One log line per HTTP request with a redacted path, plus TenantId and CorrelationId (the trace id,
    /// which the upload also passes to the worker) on every log written while the request runs.
    /// </summary>
    public static IApplicationBuilder UseDataHubRequestLogging(this IApplicationBuilder app, Func<HttpContext, Guid?> tenant)
    {
        app.Use(async (ctx, next) =>
        {
            using var correlation = LogContext.PushProperty("CorrelationId", Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier);
            await next(ctx);
        });

        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            o.GetLevel = (ctx, _, ex) =>
                ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
            o.GetMessageTemplateProperties = (ctx, _, elapsed, status) =>
            [
                new("RequestMethod", new ScalarValue(ctx.Request.Method)),
                new("RequestPath", new ScalarValue(SensitiveData.RedactPath(ctx.Request.Path))),
                new("StatusCode", new ScalarValue(status)),
                new("Elapsed", new ScalarValue(elapsed)),
            ];
            o.EnrichDiagnosticContext = (diagnostics, ctx) =>
            {
                if (tenant(ctx) is { } id) diagnostics.Set("TenantId", id);
            };
        });
        return app;
    }

    /// <summary>Adds TenantId to every log written after this point in the pipeline (place it after authentication).</summary>
    public static IApplicationBuilder UseTenantLogContext(this IApplicationBuilder app, Func<HttpContext, Guid?> tenant) =>
        app.Use(async (ctx, next) =>
        {
            if (tenant(ctx) is not { } id)
            {
                await next(ctx);
                return;
            }
            Activity.Current?.SetTag("datahub.tenant_id", id);
            using var _ = LogContext.PushProperty("TenantId", id);
            await next(ctx);
        });
}
