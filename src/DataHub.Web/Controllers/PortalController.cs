using DataHub.Application.Uploads;
using DataHub.Domain;
using DataHub.Web.Portal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DataHub.Web.Controllers;

public sealed record UploadViewModel(string CompanyName, bool CsvEnabled, long MaxSizeBytes, CustomerStatus Status);

public sealed record StatusViewModel(string CompanyName, CustomerStatus Status);

/// <summary>Pages for a verified customer. Requires the session cookie (global fallback policy).</summary>
public sealed class PortalController(UploadService uploads, IOptions<UploadOptions> options) : Controller
{
    [HttpGet("/")]
    [HttpGet("/upload")]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var customer = User.Customer();
        var status = await uploads.GetStatusAsync(customer, ct);
        if (status.JobStatus is JobStatus.Queued or JobStatus.Processing)
            return Redirect("/status");

        return View(new UploadViewModel(customer.CompanyName, options.Value.EntriesCsvEnabled, options.Value.MaxSizeBytes, status));
    }

    [HttpGet("/status")]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        View(new StatusViewModel(User.Customer().CompanyName, await uploads.GetStatusAsync(User.Customer(), ct)));

    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return View("SignedOut");
    }
}
