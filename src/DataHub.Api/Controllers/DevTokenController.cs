using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DataHub.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DataHub.Api.Controllers;

/// <summary>
/// Issues test tokens in the Local environment only, standing in for TBC's identity provider.
/// Not mapped at all in any other environment.
/// </summary>
[ApiController]
[Route("dev/token")]
[AllowAnonymous]
[ApiExplorerSettings(GroupName = "dev")]
public sealed class DevTokenController(IOptions<ApiAuthOptions> options, IHostEnvironment env) : ControllerBase
{
    private const string DefaultClientId = "tbc-los";
    private const string DefaultScope = "datahub.invitations datahub.reports";

    // [DefaultValue] makes Swagger pre-fill working values instead of "string".
    public sealed record DevTokenRequest(
        [property: DefaultValue(DefaultClientId)] string ClientId = DefaultClientId,
        [property: DefaultValue(DefaultScope)] string Scope = DefaultScope);

    [HttpPost]
    public IActionResult Issue(DevTokenRequest request)
    {
        if (!env.IsEnvironment(ApiAuthentication.LocalEnvironment) || string.IsNullOrEmpty(options.Value.DevSigningKey))
            return NotFound();

        var token = new JwtSecurityToken(
            issuer: ApiAuthentication.DevIssuer,
            audience: options.Value.Audience ?? "datahub-api",
            claims: [new Claim("client_id", request.ClientId), new Claim("scope", request.Scope)],
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: new SigningCredentials(ApiAuthentication.DevKey(options.Value.DevSigningKey), SecurityAlgorithms.HmacSha256));

        return Ok(new { access_token = new JwtSecurityTokenHandler().WriteToken(token), token_type = "Bearer", expires_in = 8 * 3600 });
    }
}
