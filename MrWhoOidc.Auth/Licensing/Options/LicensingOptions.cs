using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Licensing;

namespace MrWhoOidc.Auth.Licensing.Options;

public sealed record LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>
    /// Primary licensing public key PEM content used for signature validation.
    /// When omitted, the validator may still fall back to the embedded key if enabled.
    /// </summary>
    public string? PublicKeyPem { get; init; }

    /// <summary>
    /// Optional filesystem path to the primary licensing public key PEM.
    /// </summary>
    public string? PublicKeyPemPath { get; init; }

    /// <summary>
    /// Optional JWKS JSON document providing one or more trusted primary signing keys.
    /// </summary>
    public string? PrimaryJwksJson { get; init; }

    /// <summary>
    /// When true, fall back to the embedded production public key if no explicit primary key or JWKS is configured.
    /// </summary>
    public bool UseEmbeddedPublicKeyFallback { get; init; } = true;

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

    /// <summary>
    /// Optional additional JWKS JSON document for trusting extra issuers/keys, such as standalone licensing service environments.
    /// </summary>
    public string? AdditionalJwksJson { get; set; }

    /// <summary>
    /// Optional additional trusted issuer values beyond the built-in legacy and KeyGen issuers.
    /// </summary>
    public string[] AdditionalTrustedIssuers { get; init; } = [];
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
