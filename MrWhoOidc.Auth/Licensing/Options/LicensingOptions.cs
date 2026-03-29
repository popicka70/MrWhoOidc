using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Licensing;

namespace MrWhoOidc.Auth.Licensing.Options;

public sealed record LicensingOptions
{
    public const string SectionName = "Licensing";

    public string PublicKeyPem => EmbeddedLicensingKeys.PrimaryPublicKeyPem;

    public int CacheExpirationMinutes { get; init; } = 5;

    public int GracePeriodDays { get; init; } = 7;

    public bool StrictValidation { get; init; } = true;

    public string DefaultTier { get; init; } = "community";

    public string? PlatformIssuer { get; set; }

    /// <summary>
    /// Optional additional ECDSA P-256 public key in PEM format for license validation.
    /// When set, the license validator trusts this key in addition to the embedded production key.
    /// Intended for test/E2E environments that generate licenses with a dedicated test keypair.
    /// </summary>
    public string? AdditionalPublicKeyPem { get; set; }
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
