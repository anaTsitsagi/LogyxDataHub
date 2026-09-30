using System.Threading.RateLimiting;
using DataHub.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DataHub.Web.Portal;

public static class PortalRateLimits
{
    public const string Verification = "verification";

    /// <summary>Per client IP: limits link probing and code guessing on the anonymous pages.</summary>
    public static IServiceCollection AddPortalRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Verification, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        });
}

/// <summary>Business errors from the upload endpoints become JSON with a stable code and bilingual text.</summary>
public sealed class PortalApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not DataHubException ex) return;

        context.Result = new ObjectResult(new { code = ex.Code, message = PortalText.Error(ex.Code) })
        {
            StatusCode = ex.Kind switch
            {
                ErrorKind.NotFound => StatusCodes.Status404NotFound,
                ErrorKind.Conflict => StatusCodes.Status409Conflict,
                ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
                ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest,
            },
        };
        context.ExceptionHandled = true;
    }
}
