using DataHub.Application.Invitations;
using DataHub.Application.Processing;
using DataHub.Application.Security;
using DataHub.Application.Uploads;
using DataHub.Application.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace DataHub.Application;

public static class ApplicationRegistration
{
    /// <summary>Registers application services. Bind the option types from configuration in the host.</summary>
    public static IServiceCollection AddDataHubApplication(this IServiceCollection services)
    {
        services.AddSingleton<Secrets>();
        services.AddScoped<InvitationService>();
        services.AddScoped<VerificationService>();
        services.AddScoped<UploadService>();

        services.AddScoped<IUploadProcessor, OrisDatabaseProcessor>();
        services.AddScoped<ProcessingNotifier>();
        services.AddScoped<JobProcessor>();
        services.AddScoped<JobRecovery>();
        return services;
    }
}
