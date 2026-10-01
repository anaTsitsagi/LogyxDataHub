using System.Security.Claims;
using DataHub.Application.Verification;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DataHub.Web.Portal;

/// <summary>The verified customer is kept in an encrypted, HttpOnly session cookie.</summary>
public static class CustomerSession
{
    private const string InvitationClaim = "invitation_id";
    private const string CompanyClaim = "company_id";
    private const string TenantClaim = "tenant_id";

    public static Task SignInAsync(HttpContext http, VerifiedCustomer customer)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(InvitationClaim, customer.InvitationId.ToString()),
            new Claim(CompanyClaim, customer.CompanyId.ToString()),
            new Claim(TenantClaim, customer.TenantId.ToString()),
            new Claim(ClaimTypes.Name, customer.CompanyName),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    public static VerifiedCustomer Customer(this ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(InvitationClaim)!),
        Guid.Parse(user.FindFirstValue(CompanyClaim)!),
        Guid.Parse(user.FindFirstValue(TenantClaim)!),
        user.Identity?.Name ?? "");

    /// <summary>The signed-in customer's tenant, or null before verification.</summary>
    public static Guid? TenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TenantClaim), out var id) ? id : null;
}
