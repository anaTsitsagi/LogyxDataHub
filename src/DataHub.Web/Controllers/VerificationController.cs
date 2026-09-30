using DataHub.Application;
using DataHub.Application.Verification;
using DataHub.Web.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DataHub.Web.Controllers;

public sealed class LinkForm
{
    public string CompanyCode { get; set; } = "";
    public string? Email { get; set; }
}

public sealed record LinkViewModel(string Token, string CompanyName, bool RequiresEmail, LinkForm Form, Bilingual? Error);

public sealed record CodeViewModel(Guid ChallengeId, string Token, string Destination, Bilingual? Error);

/// <summary>Link → company details → one-time code → session.</summary>
[AllowAnonymous]
[EnableRateLimiting(PortalRateLimits.Verification)]
public sealed class VerificationController(VerificationService verification) : Controller
{
    [HttpGet("/i/{token}")]
    public async Task<IActionResult> Open(string token, CancellationToken ct)
    {
        try
        {
            var info = await verification.OpenLinkAsync(token, ct);
            return View("Link", new LinkViewModel(token, info.CompanyName, info.RequiresEmail, new LinkForm(), null));
        }
        catch (DataHubException ex)
        {
            return View("Invalid", PortalText.Error(ex.Code));
        }
    }

    [HttpPost("/i/{token}")]
    public async Task<IActionResult> Start(string token, LinkForm form, CancellationToken ct)
    {
        try
        {
            var challenge = await verification.StartAsync(token, form.CompanyCode, form.Email, ct);
            TempData["destination"] = challenge.MaskedDestination;
            return RedirectToAction(nameof(Code), new { challengeId = challenge.ChallengeId, token });
        }
        catch (DataHubException ex) when (ex.Code != "LINK_INVALID")
        {
            var info = await verification.OpenLinkAsync(token, ct);
            return View("Link", new LinkViewModel(token, info.CompanyName, info.RequiresEmail, form, PortalText.Error(ex.Code)));
        }
        catch (DataHubException ex)
        {
            return View("Invalid", PortalText.Error(ex.Code));
        }
    }

    [HttpGet("/verify/{challengeId:guid}")]
    public IActionResult Code(Guid challengeId, string token) =>
        View(new CodeViewModel(challengeId, token, TempData["destination"] as string ?? "", null));

    [HttpPost("/verify/{challengeId:guid}")]
    public async Task<IActionResult> Confirm(Guid challengeId, string token, string code, CancellationToken ct)
    {
        try
        {
            var customer = await verification.ConfirmAsync(challengeId, code, ct);
            await CustomerSession.SignInAsync(HttpContext, customer);
            return Redirect("/upload");
        }
        catch (DataHubException ex)
        {
            return View("Code", new CodeViewModel(challengeId, token, "", PortalText.Error(ex.Code)));
        }
    }
}
