using DataHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataHub.Application.Processing;

/// <summary>Tells the customer about a failed upload on the channels TBC LOS chose for the invitation.</summary>
public sealed class ProcessingNotifier(IDataHubDb db, ISmsSender sms, IEmailSender email, ILogger<ProcessingNotifier> logger)
{
    public async Task ProcessingFailedAsync(Guid invitationId, string errorCode, CancellationToken ct)
    {
        var invitation = await db.Invitations.AsNoTracking().Include(i => i.Company).SingleAsync(i => i.Id == invitationId, ct);

        // Best effort per channel: a notification outage must not change the job's outcome.
        if (invitation.Channels.HasFlag(NotificationChannels.Email) && invitation.Email is { } to)
        {
            try
            {
                var (subject, body) = Messages.ProcessingFailedEmail(invitation.Company.Name, errorCode);
                await email.SendAsync(to, subject, body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failure email for invitation {InvitationId} could not be sent", invitationId);
            }
        }

        if (invitation.Channels.HasFlag(NotificationChannels.Sms) && invitation.Phone is { } phone)
        {
            try
            {
                await sms.SendAsync(phone, Messages.ProcessingFailedSms(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failure SMS for invitation {InvitationId} could not be sent", invitationId);
            }
        }
    }
}
