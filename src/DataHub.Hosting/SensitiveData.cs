using Microsoft.AspNetCore.Http;

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
