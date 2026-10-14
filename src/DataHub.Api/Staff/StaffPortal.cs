using System.ComponentModel.DataAnnotations;
using DataHub.Api.Auth;
using DataHub.Application.Invitations;
using DataHub.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DataHub.Api.Staff;

public sealed class StaffPortalOptions
{
    public const string Section = "StaffPortal";

    /// <summary>Mailpit's web inbox, linked from the pages so the tester can open the sent email.</summary>
    public string? MailpitUrl { get; set; }

    /// <summary>Where the SMS outbox files appear on the tester's PC (display only).</summary>
    public string? SmsOutboxHint { get; set; }
}

/// <summary>
/// The staff portal is a local test tool, standing in for TBC LOS: it answers 404 in every environment
/// except Local, like POST /dev/token. An authorization filter, so it runs before the antiforgery check
/// (order 1000) and a POST elsewhere also gets 404, not 400.
/// </summary>
public sealed class LocalOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var env = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!env.IsEnvironment(ApiAuthentication.LocalEnvironment))
            context.Result = new NotFoundResult();
    }
}

public sealed class StaffInvitationForm
{
    [Display(Name = "Company code")]
    public string CompanyCode { get; set; } = "";

    [Display(Name = "Company name")]
    public string CompanyName { get; set; } = "";

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool SendEmail { get; set; } = true;
    public bool SendSms { get; set; } = true;

    /// <summary>Generated with the form and sent as the Idempotency-Key, so a double click or a reload sends one invitation.</summary>
    public string RequestKey { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed record StaffIndexModel(
    StaffInvitationForm Form,
    IReadOnlyList<InvitationSummary> Recent,
    string? Error,
    StaffPortalOptions Options);

public sealed record StaffDetailsModel(
    InvitationSummary Invitation,
    string Link,
    StaffPortalOptions Options,
    (DateOnly From, DateOnly To)? DataPeriod,
    InvitationResult? JustCreated = null);

public static class StaffText
{
    /// <summary>One status for the tester: the processing state once there is an upload, else the invitation's.</summary>
    public static (string Text, string Css) Status(InvitationSummary s) => s.LastJobStatus switch
    {
        JobStatus.Succeeded => ("Processed", "ok"),
        JobStatus.Failed => ($"Failed: {s.LastJobErrorCode}", "bad"),
        JobStatus.Processing => ("Processing", "busy"),
        JobStatus.Queued => ("Queued", "busy"),
        _ => s.Status switch
        {
            InvitationStatus.Sent => ("Sent, waiting for the customer", "wait"),
            InvitationStatus.Verified => ("Customer verified, no upload yet", "wait"),
            InvitationStatus.Uploaded => ("Uploaded", "busy"),
            InvitationStatus.Processed => ("Processed", "ok"),
            InvitationStatus.Failed => ("Failed", "bad"),
            InvitationStatus.Expired => ("Link expired", "bad"),
            _ => (s.Status.ToString(), "wait"),
        },
    };

    public static string Channels(NotificationChannels c) =>
        c == NotificationChannels.None ? "none"
        : string.Join(" + ", new[] { c.HasFlag(NotificationChannels.Email) ? "email" : null, c.HasFlag(NotificationChannels.Sms) ? "SMS" : null }
            .Where(x => x is not null));

    public static string Time(DateTimeOffset? t) => t?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "–";
}
