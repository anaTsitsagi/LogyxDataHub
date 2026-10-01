using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.WebEncoders;
using DataHub.Application;
using DataHub.Hosting;
using DataHub.Infrastructure;
using DataHub.Web.Portal;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.AddDataHubObservability("datahub-web");
builder.Services.AddDataHubApplication();
builder.Services.AddDataHubInfrastructure(builder.Configuration);
builder.Services.AddDataHubDataProtection("datahub-web");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "__Host-datahub";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    o.SlidingExpiration = true;
    o.LoginPath = "/signed-out";
    // The upload endpoints are called from script: answer 401 instead of redirecting.
    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/portal-api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "__Host-datahub-csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
// Emit Georgian as-is instead of &#x....; entities (Razor encodes non-Latin text by default).
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddPortalRateLimits();
builder.Services.AddHealthChecks().AddDataHubReadiness();

var app = builder.Build();

app.UseExceptionHandler("/error");
// Friendly error pages for browser navigation only; the JSON upload API keeps its raw status codes.
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/portal-api"),
    branch => branch.UseStatusCodePagesWithReExecute("/error/{0}"));
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer"; // the link token is in the URL; never leak it to other sites
    h.XFrameOptions = "DENY";
    await next();
});
app.UseStaticFiles();
app.UseDataHubRequestLogging(ctx => ctx.User.TenantId());
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseTenantLogContext(ctx => ctx.User.TenantId());
app.UseAuthorization();

// Liveness runs no checks (the process answers); readiness also needs the database.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program;
