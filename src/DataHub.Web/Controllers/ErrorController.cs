using DataHub.Web.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataHub.Web.Controllers;

[AllowAnonymous]
[IgnoreAntiforgeryToken] // status-code re-execution keeps the original method (often POST)
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ErrorController : Controller
{
    [Route("/error/{status:int?}")]
    public IActionResult Show(int? status) => status switch
    {
        404 => View("Invalid", PortalText.Error("LINK_INVALID")),
        429 => View("Invalid", PortalText.Error("OTP_RATE_LIMITED")),
        _ => View("Invalid", PortalText.Error(null)),
    };

    [HttpGet("/signed-out")]
    public IActionResult SignedOut() => View("~/Views/Portal/SignedOut.cshtml");
}
