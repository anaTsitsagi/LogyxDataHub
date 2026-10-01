using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace DataHub.Hosting;

/// <summary>Keeps secrets that travel in URLs (the invitation link token) out of logs and traces.</summary>
public static class SensitiveData
{
    public const string Redacted = "***";

    /// <summary>Paths whose next segment is a secret: <c>/i/{token}</c> is the customer's invitation link.</summary>
    private static readonly PathString[] SecretSegmentPrefixes = ["/i"];

    public static string RedactPath(PathString path)
    {
        foreach (var prefix in SecretSegmentPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out var rest) && rest.HasValue && rest != "/")
                return $"{prefix}/{Redacted}";
        }
        return path.Value ?? "";
    }
}

/// <summary>
/// Redacts the <c>RequestPath</c> property on every event. ASP.NET Core's request scope adds the raw path to
/// every log written during a request (e.g. "Email sent" while handling <c>POST /i/{token}</c>).
/// </summary>
public sealed class RequestPathRedactor : ILogEventEnricher
{
    public const string PropertyName = "RequestPath";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue(PropertyName, out var value) && value is ScalarValue { Value: string path })
        {
            string redacted = SensitiveData.RedactPath(new PathString(path));
            if (redacted != path)
                logEvent.AddOrUpdateProperty(new LogEventProperty(PropertyName, new ScalarValue(redacted)));
        }
    }
}
