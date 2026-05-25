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

        if (!settings.Enabled)
        {
            logger.LogWarning("Mail delivery is disabled. Configure Mail:Enabled=true to send registration and email-confirmation messages.");
        }

        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            logger.LogWarning("Mail delivery is enabled but Mail:SmtpHost is not configured.");
        }
        else if (IsLocalhost(settings.SmtpHost))
        {
            logger.LogWarning(
                "Mail SMTP host is configured as {Host}. This is usually only valid for local development or an in-container mail catcher; production deployments should configure Mail:SmtpHost.",
                settings.SmtpHost);
        }

        if (settings.SmtpPort <= 0)
        {
            logger.LogWarning("Mail delivery is enabled but Mail:SmtpPort is invalid: {Port}", settings.SmtpPort);
        }

        if (string.IsNullOrWhiteSpace(settings.FromAddress))
        {
            logger.LogWarning("Mail delivery is enabled but Mail:FromAddress is not configured.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsLocalhost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
}
