using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Antiforgery;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services; // IJarReplayCache
using MrWhoOidc.Security;
using StackExchange.Redis;
using MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;
using MrWhoOidc.WebAuth.Infrastructure; // RedisJarReplayCache

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Extracted security core registration previously in Program.cs.
/// Contains DPoP, JAR replay cache overrides, token exchange limiter, DataProtection persistence,
/// certificate forwarding. (Antiforgery + localization moved to AddLocalizationAndMvc.) Pure mechanical move (no behavior change intended).
/// </summary>
public static class SecurityCoreExtensions
{
    public static IServiceCollection AddMrWhoOidcSecurityCore(this IServiceCollection services, IConfiguration configuration, IConnectionMultiplexer? redisMux, IHostEnvironment? environment = null)
    {
        // Token Exchange limiter options + default in-memory implementation (overridden below if Redis present)
        services.Configure<TokenExchangeRateLimitOptions>(configuration.GetSection("TokenExchangeRateLimit"));
        services.AddSingleton<ITokenExchangeRateLimiter, InMemoryTokenExchangeRateLimiter>();

        // DPoP validator and replay/nonce stores
        services.Configure<DPoPValidationOptions>(options =>
        {
            options.IatLeewaySeconds = configuration.GetValue<int?>("Auth:DpopIatLeewaySeconds") ?? 60;
        });
        services.AddSingleton<IDPoPValidator, DPoPValidator>();
        if (redisMux is not null)
        {
            services.AddSingleton<IDPoPReplayCache, RedisDPoPReplayCache>();
            services.AddSingleton<IDPoPNonceStore, RedisDPoPNonceStore>();
        }
        else
        {
            services.AddSingleton<IDPoPReplayCache, InMemoryDPoPReplayCache>();
            services.AddSingleton<IDPoPNonceStore, InMemoryDPoPNonceStore>();
        }

        // Redis-specific overrides (JAR replay cache + Token Exchange limiter)
        if (redisMux is not null)
        {
            services.AddSingleton<IJarReplayCache, RedisJarReplayCache>();
            services.AddSingleton<ITokenExchangeRateLimiter, RedisTokenExchangeRateLimiter>();
        }

        // DataProtection -> DB persistence (required for antiforgery stability across restarts)
        var dataProtection = services.AddDataProtection().PersistKeysToDbContext<AuthDbContext>();

        // Optionally encrypt the DataProtection key-ring at rest with an X.509 certificate. Without
        // this the key-ring is stored UNENCRYPTED in the same database as the signing keys it
        // protects, so a single DB compromise yields both the wrapped private signing keys and the
        // means to unwrap them. Set DataProtection:CertificatePath (+ optional CertificatePassword)
        // in production — or wire a KMS/Key Vault key-protection provider — to close that gap.
        var dpCertPath = configuration["DataProtection:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(dpCertPath) && File.Exists(dpCertPath))
        {
            var dpCert = X509CertificateLoader.LoadPkcs12FromFile(
                dpCertPath, configuration["DataProtection:CertificatePassword"]);
            dataProtection.ProtectKeysWithCertificate(dpCert);
        }
        else if (environment is not null && !environment.IsDevelopment() && !environment.IsStaging())
        {
            // Production without a DataProtection certificate: the key-ring is stored unencrypted
            // in the same database as the signing keys it protects. A single DB compromise yields
            // both the wrapped private signing keys and the means to unwrap them.
            // Log a critical warning — operators should set DataProtection:CertificatePath in production.
            Console.Error.WriteLine(
                "[CRITICAL] DataProtection key-ring is NOT encrypted at rest. "
                + "Set DataProtection:CertificatePath (and CertificatePassword) in production "
                + "to protect signing keys against DB compromise.");
        }

        // (Antiforgery + localization registrations now live in AddLocalizationAndMvc)

        if (configuration.GetValue<bool>("Security:CertificateForwarding:Enabled"))
        {
            services.AddCertificateForwarding(o => o.CertificateHeader = "X-Client-Cert");
        }

        return services;
    }
}
