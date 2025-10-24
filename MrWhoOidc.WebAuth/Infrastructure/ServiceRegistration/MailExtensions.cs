using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        return services;
    }
}
