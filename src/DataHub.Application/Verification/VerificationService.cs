using System.Security.Cryptography;
using DataHub.Application.Security;
using DataHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataHub.Application.Verification;

public sealed class OtpOptions
{
    public const string Section = "Otp";

    public int Digits { get; set; } = 6;
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum codes per invitation per hour; blocks SMS/email flooding.</summary>
    public int MaxCodesPerHour { get; set; } = 5;
}

public sealed record InvitationLinkInfo(Guid InvitationId, string CompanyName, bool RequiresEmail);

public sealed record OtpChallengeResult(Guid ChallengeId, NotificationChannels Channel, string MaskedDestination, DateTimeOffset ExpiresAt);

/// <summary>Who the customer is after a successful verification; becomes the portal session.</summary>
public sealed record VerifiedCustomer(Guid InvitationId, Guid CompanyId, Guid TenantId, string CompanyName);

public sealed class VerificationService(
    IDataHubDb db,
    Secrets secrets,
    ISmsSender sms,
    IEmailSender email,
    IOptions<OtpOptions> options,
    TimeProvider clock,
    ILogger<VerificationService> logger)
{
    // Deliberately identical for "unknown link", "wrong company" and "wrong email" so the form reveals nothing.
    private static DataHubException InvalidLink() =>
        new(ErrorKind.NotFound, "LINK_INVALID", "The link is invalid or has expired.");

    private static DataHubException DetailsMismatch() =>
        new(ErrorKind.Validation, "DETAILS_MISMATCH", "The details do not match this link.");

    public async Task<InvitationLinkInfo> OpenLinkAsync(string token, CancellationToken ct)
    {
        var invitation = await FindActiveInvitationAsync(token, ct);
        return new InvitationLinkInfo(invitation.Id, invitation.Company.Name, invitation.Email is not null);
    }

    public async Task<OtpChallengeResult> StartAsync(string token, string companyCode, string? contactEmail, CancellationToken ct)
    {
        var invitation = await FindActiveInvitationAsync(token, ct);
        var o = options.Value;

        bool companyMatches = string.Equals(invitation.Company.CompanyCode, companyCode?.Trim(), StringComparison.Ordinal);
        bool emailMatches = invitation.Email is null
            || string.Equals(invitation.Email, contactEmail?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!companyMatches || !emailMatches)
        {
            logger.LogWarning("Verification details mismatch for invitation {InvitationId}", invitation.Id);
            throw DetailsMismatch();
        }

        var now = clock.GetUtcNow();
        var recent = await db.OtpChallenges
            .Where(c => c.InvitationId == invitation.Id && c.CreatedAt > now.AddHours(-1))
            .Select(c => c.CreatedAt)
            .ToListAsync(ct);
        if (recent.Count >= o.MaxCodesPerHour || recent.Any(t => t > now - o.ResendCooldown))
            throw new DataHubException(ErrorKind.TooManyRequests, "OTP_RATE_LIMITED", "Please wait before requesting a new code.");

        // Email is the primary OTP channel (per the architecture); SMS when the invitation has no email.
        var channel = invitation.Email is not null ? NotificationChannels.Email : NotificationChannels.Sms;
        var code = Secrets.NewOtpCode(o.Digits);
        var challenge = new OtpChallenge
        {
            Id = Guid.CreateVersion7(),
            InvitationId = invitation.Id,
            Channel = channel,
            CreatedAt = now,
            ExpiresAt = now + o.Lifetime,
        };
        challenge.CodeHash = secrets.HashOtp(challenge.Id, code);
        db.OtpChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);

        if (channel == NotificationChannels.Email)
        {
            var (subject, body) = Messages.OtpEmail(code, o.Lifetime);
            await email.SendAsync(invitation.Email!, subject, body, ct);
        }
        else
        {
            await sms.SendAsync(invitation.Phone!, Messages.OtpSms(code), ct);
        }

        var destination = channel == NotificationChannels.Email ? MaskEmail(invitation.Email!) : MaskPhone(invitation.Phone!);
        return new OtpChallengeResult(challenge.Id, channel, destination, challenge.ExpiresAt);
    }

    public async Task<VerifiedCustomer> ConfirmAsync(Guid challengeId, string code, CancellationToken ct)
    {
        var o = options.Value;
        var challenge = await db.OtpChallenges.SingleOrDefaultAsync(c => c.Id == challengeId, ct);
        var now = clock.GetUtcNow();

        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now || challenge.Attempts >= o.MaxAttempts)
            throw new DataHubException(ErrorKind.Validation, "OTP_EXPIRED", "The code has expired. Please request a new one.");

        challenge.Attempts++;
        var expected = secrets.HashOtp(challenge.Id, (code ?? "").Trim());
        if (!CryptographicOperations.FixedTimeEquals(expected, challenge.CodeHash))
        {
            await db.SaveChangesAsync(ct);
            var left = o.MaxAttempts - challenge.Attempts;
            throw new DataHubException(ErrorKind.Validation, left > 0 ? "OTP_WRONG" : "OTP_EXPIRED",
                left > 0 ? $"Incorrect code. {left} attempt(s) left." : "Too many attempts. Please request a new code.");
        }

        challenge.ConsumedAt = now;
        var invitation = await db.Invitations.Include(i => i.Company).SingleAsync(i => i.Id == challenge.InvitationId, ct);
        if (invitation.ExpiresAt <= now) throw InvalidLink();
        invitation.VerifiedAt ??= now;
        if (invitation.Status is InvitationStatus.Sent) invitation.Status = InvitationStatus.Verified;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Invitation {InvitationId} verified", invitation.Id);
        return new VerifiedCustomer(invitation.Id, invitation.CompanyId, invitation.Company.TenantId, invitation.Company.Name);
    }

    private async Task<Invitation> FindActiveInvitationAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) throw InvalidLink();
        var hash = Secrets.HashToken(token);
        var invitation = await db.Invitations.Include(i => i.Company).SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || invitation.ExpiresAt <= clock.GetUtcNow() || invitation.Status == InvitationStatus.Expired)
            throw InvalidLink();
        return invitation;
    }

    internal static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? "***" + email[Math.Max(at, 0)..] : $"{email[0]}***{email[at..]}";
    }

    internal static string MaskPhone(string phone) => phone.Length <= 4 ? "****" : $"*** *** {phone[^4..^2]} {phone[^2..]}";
}
