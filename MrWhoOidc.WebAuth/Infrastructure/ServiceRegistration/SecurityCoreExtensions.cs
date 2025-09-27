using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// antiforgery, localization, certificate forwarding. Pure mechanical move (no behavior change intended).
/// </summary>
public static class SecurityCoreExtensions
{
    public static IServiceCollection AddMrWhoOidcSecurityCore(this IServiceCollection services, IConfiguration configuration, IConnectionMultiplexer? redisMux)
    {
        // Token Exchange limiter options + default in-memory implementation (overridden below if Redis present)
        services.Configure<TokenExchangeRateLimitOptions>(configuration.GetSection("TokenExchangeRateLimit"));
        services.AddSingleton<ITokenExchangeRateLimiter, InMemoryTokenExchangeRateLimiter>();

        // DPoP validator and replay/nonce stores
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
        services.AddDataProtection().PersistKeysToDbContext<AuthDbContext>();

        // Antiforgery tokens (used by interactive flows)
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = ".mrwhooidc.af";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.FormFieldName = "__RequestVerificationToken";
            options.HeaderName = "X-CSRF-TOKEN";
        });

        // Localization resources path (Razor & validation messages)
        services.AddLocalization(o => o.ResourcesPath = "Resources");

        // Certificate forwarding (maintains prior header name)
        services.AddCertificateForwarding(o => o.CertificateHeader = "X-Client-Cert");

        return services;
    }
}
