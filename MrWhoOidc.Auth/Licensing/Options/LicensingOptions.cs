using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MrWhoOidc.Auth.Licensing.Options;

public sealed record LicensingOptions
{
    public const string SectionName = "Licensing";

    public string PublicKeyPem { get; init; } = string.Empty;

    public int CacheExpirationMinutes { get; init; } = 5;

    public int GracePeriodDays { get; init; } = 7;

    public bool StrictValidation { get; init; } = true;

    public string DefaultTier { get; init; } = "community";
}

public static class LicensingOptionsExtensions
{
    public static IServiceCollection AddLicensingOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<LicensingOptions>()
            .Bind(configuration.GetSection(LicensingOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}
