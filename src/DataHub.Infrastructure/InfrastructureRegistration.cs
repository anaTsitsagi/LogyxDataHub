using DataHub.Application;
using DataHub.Application.Invitations;
using DataHub.Application.Security;
using DataHub.Application.Uploads;
using DataHub.Application.Verification;
using DataHub.Infrastructure.Messaging;
using DataHub.Infrastructure.Notifications;
using DataHub.Infrastructure.Persistence;
using DataHub.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataHub.Infrastructure;

public static class InfrastructureRegistration
{
    /// <summary>Registers persistence, S3, RabbitMQ, email and SMS, with options bound from configuration.</summary>
    public static IServiceCollection AddDataHubInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var database = configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
        database.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? database.ConnectionString;
        services.AddDataHubPersistence(database);

        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.Section));
        services.Configure<InvitationOptions>(configuration.GetSection(InvitationOptions.Section));
        services.Configure<OtpOptions>(configuration.GetSection(OtpOptions.Section));
        services.Configure<UploadOptions>(configuration.GetSection(UploadOptions.Section));
        services.Configure<S3Options>(configuration.GetSection(S3Options.Section));
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.Section));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.Section));
        services.Configure<SmsOptions>(configuration.GetSection(SmsOptions.Section));

        services.AddSingleton<IFileStore, S3FileStore>();
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IJobPublisher, RabbitMqJobPublisher>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        var smsProvider = configuration[$"{SmsOptions.Section}:Provider"] ?? "Outbox";
        if (!smsProvider.Equals("Outbox", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SMS provider '{smsProvider}' is not supported yet (waiting for TBC's gateway API).");
        services.AddSingleton<ISmsSender, OutboxSmsSender>();

        return services;
    }
}
