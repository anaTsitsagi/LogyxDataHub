using System.Net.Mail;
using System.Text.RegularExpressions;
using DataHub.Application.Security;
using DataHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Invitations;

public sealed class InvitationOptions
{
    public const string Section = "Invitations";

    /// <summary>Public base URL of the customer portal, e.g. https://datahub.tbc.ge.</summary>
    public string PortalBaseUrl { get; set; } = "";
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromDays(14);
}

public sealed record CreateInvitationCommand(
    string CompanyCode,
    string CompanyName,
    string? Email,
    string? Phone,
    NotificationChannels Channels,
    string? IdempotencyKey,
    string ClientId);

public sealed record InvitationResult(
    Guid InvitationId,
    Guid TenantId,
    string Link,
    DateTimeOffset ExpiresAt,
    InvitationStatus Status,
    NotificationChannels DeliveredChannels,
    bool Replayed);

public sealed record InvitationStatusResult(
    Guid InvitationId,
    Guid TenantId,
    InvitationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? VerifiedAt,
    JobStatus? LastJobStatus,
    string? LastJobErrorCode,
    DateTimeOffset? LastJobFinishedAt);

public sealed partial class InvitationService(
    IDataHubDb db,
    Secrets secrets,
    ISmsSender sms,
    IEmailSender email,
    IOptions<InvitationOptions> options,
    TimeProvider clock,
    ILogger<InvitationService> logger)
{
    public async Task<InvitationResult> CreateAsync(CreateInvitationCommand cmd, CancellationToken ct)
    {
        var (companyCode, contactEmail, phone) = Validate(cmd);

        if (cmd.IdempotencyKey is not null)
        {
            var existing = await db.Invitations.Include(i => i.Company)
                .SingleOrDefaultAsync(i => i.CreatedByClient == cmd.ClientId && i.IdempotencyKey == cmd.IdempotencyKey, ct);
            if (existing is not null)
                return ToResult(existing, existing.Channels, replayed: true);
        }

        var now = clock.GetUtcNow();
        var company = await db.Companies.SingleOrDefaultAsync(c => c.CompanyCode == companyCode, ct);
        if (company is null)
        {
            company = new Company
            {
                Id = Guid.CreateVersion7(), TenantId = Guid.NewGuid(), CompanyCode = companyCode,
                Name = cmd.CompanyName.Trim(), CreatedAt = now,
            };
            db.Companies.Add(company);
        }
        else if (!string.IsNullOrWhiteSpace(cmd.CompanyName))
        {
            company.Name = cmd.CompanyName.Trim();
        }

        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(),
            Company = company,
            CompanyId = company.Id,
            Email = contactEmail,
            Phone = phone,
            Channels = cmd.Channels,
            Status = InvitationStatus.Sent,
            IdempotencyKey = cmd.IdempotencyKey,
            CreatedByClient = cmd.ClientId,
            CreatedAt = now,
            ExpiresAt = now + options.Value.LinkLifetime,
        };
        invitation.TokenHash = Secrets.HashToken(secrets.InvitationToken(invitation.Id));
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        var delivered = await DeliverLinkAsync(invitation, ct);
        logger.LogInformation("Invitation {InvitationId} created for tenant {TenantId} by {ClientId}; delivered via {Channels}",
            invitation.Id, company.TenantId, cmd.ClientId, delivered);
        return ToResult(invitation, delivered, replayed: false);
    }

    public async Task<InvitationStatusResult> GetStatusAsync(Guid invitationId, string clientId, CancellationToken ct)
    {
        var invitation = await db.Invitations.Include(i => i.Company).AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == invitationId && i.CreatedByClient == clientId, ct)
            ?? throw new DataHubException(ErrorKind.NotFound, "INVITATION_NOT_FOUND", "Invitation not found.");

        var job = await (from u in db.Uploads
                         join j in db.ProcessingJobs on u.Id equals j.UploadId
                         where u.InvitationId == invitationId
                         orderby j.QueuedAt descending
                         select j).AsNoTracking().FirstOrDefaultAsync(ct);

        var status = invitation.Status is InvitationStatus.Sent && invitation.ExpiresAt <= clock.GetUtcNow()
            ? InvitationStatus.Expired
            : invitation.Status;

        return new InvitationStatusResult(invitation.Id, invitation.Company.TenantId, status, invitation.CreatedAt,
            invitation.ExpiresAt, invitation.VerifiedAt, job?.Status, job?.ErrorCode, job?.FinishedAt);
    }

    public string LinkFor(Guid invitationId) =>
        $"{options.Value.PortalBaseUrl.TrimEnd('/')}/i/{secrets.InvitationToken(invitationId)}";

    private InvitationResult ToResult(Invitation i, NotificationChannels delivered, bool replayed) =>
        new(i.Id, i.Company.TenantId, LinkFor(i.Id), i.ExpiresAt, i.Status, delivered, replayed);

    private async Task<NotificationChannels> DeliverLinkAsync(Invitation invitation, CancellationToken ct)
    {
        var link = LinkFor(invitation.Id);
        var delivered = NotificationChannels.None;

        if (invitation.Channels.HasFlag(NotificationChannels.Sms))
        {
            try
            {
                await sms.SendAsync(invitation.Phone!, Messages.InvitationSms(link), ct);
                delivered |= NotificationChannels.Sms;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SMS delivery failed for invitation {InvitationId}", invitation.Id);
            }
        }

        if (invitation.Channels.HasFlag(NotificationChannels.Email))
        {
            try
            {
                var (subject, body) = Messages.InvitationEmail(invitation.Company.Name, link, invitation.ExpiresAt);
                await email.SendAsync(invitation.Email!, subject, body, ct);
                delivered |= NotificationChannels.Email;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Email delivery failed for invitation {InvitationId}", invitation.Id);
            }
        }

        return delivered;
    }

    private static (string CompanyCode, string? Email, string? Phone) Validate(CreateInvitationCommand cmd)
    {
        var errors = new List<string>();
        var companyCode = cmd.CompanyCode?.Trim() ?? "";
        if (!CompanyCodeRegex().IsMatch(companyCode)) errors.Add("companyCode must be 9 or 11 digits.");
        if (string.IsNullOrWhiteSpace(cmd.CompanyName) || cmd.CompanyName.Length > 300) errors.Add("companyName is required (max 300).");
        if (cmd.Channels == NotificationChannels.None) errors.Add("channels must contain 'sms' and/or 'email'.");
        if (cmd.IdempotencyKey is { Length: > 100 }) errors.Add("Idempotency-Key must be at most 100 characters.");

        string? email = null;
        if (!string.IsNullOrWhiteSpace(cmd.Email))
        {
            email = cmd.Email.Trim();
            if (email.Length > 320 || !MailAddress.TryCreate(email, out _)) errors.Add("email is not a valid address.");
        }
        else if (cmd.Channels.HasFlag(NotificationChannels.Email)) errors.Add("email is required for the 'email' channel.");

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(cmd.Phone))
        {
            phone = NormalizePhone(cmd.Phone);
            if (phone is null) errors.Add("phone must be a Georgian mobile number (e.g. +995 5XX XXX XXX).");
        }
        else if (cmd.Channels.HasFlag(NotificationChannels.Sms)) errors.Add("phone is required for the 'sms' channel.");

        if (errors.Count > 0)
            throw new DataHubException(ErrorKind.Validation, "INVITATION_INVALID", string.Join(" ", errors));
        return (companyCode, email, phone);
    }

    /// <summary>Normalizes Georgian mobile numbers to E.164 (+9955XXXXXXXX).</summary>
    internal static string? NormalizePhone(string raw)
    {
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("995")) digits = digits[3..];
        return digits.Length == 9 && digits[0] == '5' ? "+995" + digits : null;
    }

    [GeneratedRegex(@"^(\d{9}|\d{11})$")]
    private static partial Regex CompanyCodeRegex();
}
