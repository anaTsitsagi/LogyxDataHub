using DataHub.Application;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DataHub.Infrastructure.Notifications;

public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;

    /// <summary>"StartTls", "SslOnConnect" or "None" (local test servers only).</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "TBC DataHub";
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = textBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, o.Security, ct);
        if (!string.IsNullOrEmpty(o.Username))
            await client.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
        logger.LogInformation("Email sent: {Subject}", subject);
    }
}

public sealed class SmsOptions
{
    public const string Section = "Sms";

    /// <summary>"Outbox" writes messages to files (local testing). The TBC gateway adapter is added once its API is known.</summary>
    public string Provider { get; set; } = "Outbox";
    public string OutboxDirectory { get; set; } = "/tmp/sms-outbox";
}

/// <summary>
/// Local stand-in for the SMS gateway: each message becomes a text file, so testers can open the
/// link or read the code. Never enabled outside local/test environments.
/// </summary>
public sealed class OutboxSmsSender(IOptions<SmsOptions> options, TimeProvider clock, ILogger<OutboxSmsSender> logger) : ISmsSender
{
    public async Task SendAsync(string phone, string text, CancellationToken ct)
    {
        var dir = options.Value.OutboxDirectory;
        Directory.CreateDirectory(dir);
        // The file name is logged, so it carries no phone number; the recipient is inside the file.
        var file = Path.Combine(dir, $"{clock.GetUtcNow():yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, $"To: {phone}\n\n{text}\n", ct);
        logger.LogInformation("SMS written to outbox {File}", Path.GetFileName(file));
    }
}
