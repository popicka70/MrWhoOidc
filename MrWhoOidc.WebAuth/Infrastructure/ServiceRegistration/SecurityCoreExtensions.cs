using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Antiforgery;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services; // IJarReplayCache
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Registers the security infrastructure that depends on the selected distributed state backend:
/// DPoP validator with replay/nonce stores, the JAR replay cache, the token-exchange limiter,
/// DataProtection persistence and certificate forwarding.
/// </summary>
public static class SecurityCoreExtensions
{
    public static IServiceCollection AddMrWhoOidcSecurityCore(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        // Token Exchange limiter options + store-backed implementation.
        services.Configure<TokenExchangeRateLimitOptions>(configuration.GetSection("TokenExchangeRateLimit"));
        services.AddSingleton<ITokenExchangeRateLimiter, DistributedTokenExchangeRateLimiter>();

        // DPoP validator and replay/nonce stores over the selected distributed state backend.
        services.Configure<DPoPValidationOptions>(options =>
        {
            options.IatLeewaySeconds = configuration.GetValue<int?>("Auth:DpopIatLeewaySeconds") ?? 60;
        });
        services.AddSingleton<IDPoPValidator, DPoPValidator>();
        services.AddSingleton<IDPoPReplayCache, StoreDPoPReplayCache>();
        services.AddSingleton<IDPoPNonceStore, StoreDPoPNonceStore>();

        // JAR replay cache over the same backend. Auth core registers an in-memory default; replace
        // it so every consumer sees the distributed implementation.
        services.Replace(ServiceDescriptor.Singleton<IJarReplayCache, StoreJarReplayCache>());

        // DataProtection -> DB persistence (required for antiforgery stability across restarts)
        var dataProtection = services.AddDataProtection().PersistKeysToDbContext<AuthDbContext>();

        // Optionally encrypt the DataProtection key-ring at rest with an X.509 certificate. Without
        // this the key-ring is stored UNENCRYPTED in the same database as the signing keys it
        // protects, so a single DB compromise yields both the wrapped private signing keys and the
        // means to unwrap them. Set DataProtection:CertificatePath or CertificateBase64 (+ optional
        // CertificatePassword) in production — or wire a KMS/Key Vault key-protection provider —
        // to close that gap.
        var dpCertPath = configuration["DataProtection:CertificatePath"];
        var dpCertBase64 = configuration["DataProtection:CertificateBase64"];
        X509Certificate2? dpCert = null;

        if (!string.IsNullOrWhiteSpace(dpCertPath) && File.Exists(dpCertPath))
        {
            dpCert = X509CertificateLoader.LoadPkcs12FromFile(
                dpCertPath, configuration["DataProtection:CertificatePassword"]);
        }
        else if (!string.IsNullOrWhiteSpace(dpCertBase64))
        {
            dpCert = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(dpCertBase64),
                configuration["DataProtection:CertificatePassword"]);
        }

        if (dpCert is not null)
        {
            dataProtection.ProtectKeysWithCertificate(dpCert);
        }
        else if (environment is not null && !environment.IsDevelopment() && !environment.IsStaging())
        {
            // Production without a DataProtection certificate: the key-ring would be stored unencrypted
            // in the same database as the signing keys it protects. A single DB compromise yields
            // both the wrapped private signing keys and the means to unwrap them.
            // Fail closed: refuse to start unless the operator explicitly accepts the risk.
            if (!configuration.GetValue<bool>("DataProtection:AllowUnencryptedKeyRingInProduction"))
            {
                throw new InvalidOperationException(
                    "DataProtection key-ring would be stored UNENCRYPTED at rest in production. "
                    + "Set DataProtection:CertificatePath or DataProtection:CertificateBase64, along with "
                    + "DataProtection:CertificatePassword, to encrypt the key-ring with an X.509 certificate, "
                    + "or, if you explicitly accept the risk of storing the key-ring unencrypted in the same "
                    + "database as the signing keys it protects, "
                    + "set DataProtection:AllowUnencryptedKeyRingInProduction=true.");
            }
        }

        // (Antiforgery + localization registrations now live in AddLocalizationAndMvc)

        if (configuration.GetValue<bool>("Security:CertificateForwarding:Enabled"))
        {
            services.AddCertificateForwarding(o =>
            {
                o.CertificateHeader = Pipeline.ForwardedClientCertificate.HeaderName;
                o.HeaderConverter = Pipeline.ForwardedClientCertificate.Parse!;
            });
        }

        return services;
    }
}
