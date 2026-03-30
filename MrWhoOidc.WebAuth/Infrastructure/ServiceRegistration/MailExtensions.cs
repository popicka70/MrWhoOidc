using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

public static class MailExtensions
{
    public static IServiceCollection AddMrWhoOidcMail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MailOptions>();
        services.Configure<MailOptions>(configuration.GetSection("Mail"));

        services.Replace(ServiceDescriptor.Singleton<IEmailSender, SmtpEmailSender>());
        services.AddScoped<IEmailConfirmationWorkflow, EmailConfirmationWorkflow>();
        services.AddHostedService<MailConfigurationHostedService>();

        return services;
    }
}

internal sealed class MailConfigurationHostedService(
    IOptions<MailOptions> options,
    ILogger<MailConfigurationHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        logger.LogInformation(
            "Mail configuration loaded: Enabled={Enabled}, Host={Host}, Port={Port}, UseSsl={UseSsl}, FromAddress={FromAddress}, FromName={FromName}, UsernameConfigured={HasUsername}, PasswordConfigured={HasPassword}",
            settings.Enabled,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.UseSsl,
            settings.FromAddress,
            settings.FromName,
            !string.IsNullOrWhiteSpace(settings.Username),
            !string.IsNullOrWhiteSpace(settings.Password));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
