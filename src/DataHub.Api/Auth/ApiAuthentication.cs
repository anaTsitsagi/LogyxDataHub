using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace DataHub.Api.Auth;

public sealed class ApiAuthOptions
{
    public const string Section = "Auth";

    /// <summary>OIDC authority of TBC's identity provider (tokens are validated against its signing keys).</summary>
    public string? Authority { get; set; }
    public string? Audience { get; set; }

    /// <summary>Scope required to manage invitations, if TBC's IdP issues scopes.</summary>
    public string? InvitationsScope { get; set; }

    /// <summary>Scope required to read reports, if TBC's IdP issues scopes.</summary>
    public string? ReportsScope { get; set; }

    /// <summary>Local environment only: symmetric key for self-issued test tokens.</summary>
    public string? DevSigningKey { get; set; }
}

public static class ApiAuthentication
{
    public const string LocalEnvironment = "Local";
    public const string DevIssuer = "datahub-local";
    public const string InvitationsPolicy = "invitations";
    public const string ReportsPolicy = "reports";

    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var o = configuration.GetSection(ApiAuthOptions.Section).Get<ApiAuthOptions>() ?? new ApiAuthOptions();
        bool devTokens = env.IsEnvironment(LocalEnvironment) && !string.IsNullOrEmpty(o.DevSigningKey);
        if (string.IsNullOrWhiteSpace(o.Authority) && !devTokens)
            throw new InvalidOperationException("Auth:Authority must be configured (dev tokens are only allowed in the Local environment).");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            if (devTokens)
            {
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = DevIssuer,
                    ValidAudience = o.Audience ?? "datahub-api",
                    IssuerSigningKey = DevKey(o.DevSigningKey!),
                };
            }
            else
            {
                jwt.Authority = o.Authority;
                jwt.Audience = o.Audience;
                jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(o.Audience);
            }
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(InvitationsPolicy, p =>
            {
                p.RequireAuthenticatedUser();
                if (!string.IsNullOrEmpty(o.InvitationsScope))
                    p.RequireAssertion(ctx => HasScope(ctx.User, o.InvitationsScope));
            })
            .AddPolicy(ReportsPolicy, p =>
            {
                p.RequireAuthenticatedUser();
                if (!string.IsNullOrEmpty(o.ReportsScope))
                    p.RequireAssertion(ctx => HasScope(ctx.User, o.ReportsScope));
            });

        return services;
    }

    public static SymmetricSecurityKey DevKey(string key) => new(Encoding.UTF8.GetBytes(key.PadRight(32, '#')));

    /// <summary>The calling system (e.g. LOS) as identified by its client-credentials token.</summary>
    public static string ClientId(this ClaimsPrincipal user) =>
        user.FindFirstValue("client_id") ?? user.FindFirstValue("azp") ?? user.FindFirstValue("appid")
        ?? user.FindFirstValue("sub") ?? throw new InvalidOperationException("Token has no client identifier.");

    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope);
}
