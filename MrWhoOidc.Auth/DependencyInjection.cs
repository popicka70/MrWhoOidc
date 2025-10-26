using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
using Fido2NetLib;

namespace MrWhoOidc.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddMrWhoOidcAuthCore(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Multi-tenancy support (if configuration provided)
        if (configuration != null)
        {
            services.Configure<MultiTenancyOptions>(configuration.GetSection("MultiTenancy"));
            services.AddSingleton<IMultiTenancyOptions>(sp =>
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiTenancyOptions>>().Value);
        }
        else
        {
            // Default: single-tenant mode for tests
            services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions { Enabled = false, DefaultTenantSlug = "default" });
        }

        // Memory cache needed by TenantResolver
        services.AddMemoryCache();

        // HybridCache (required by services like ClientStore, UserService, TenantSettingsService, etc.)
        // In production, WebAuth will override this with a Redis-backed version, but for unit tests
        // and basic scenarios, this provides a default memory-only hybrid cache implementation.
        services.AddHybridCache();

        services.AddOptions<EmailConfirmationOptions>();
        if (configuration != null)
        {
            services.Configure<EmailConfirmationOptions>(configuration.GetSection("EmailConfirmation"));
        }
        else
        {
            services.Configure<EmailConfirmationOptions>(_ => { });
        }

        services.TryAddSingleton<IEmailSender, NullEmailSender>();
        services.AddScoped<IEmailConfirmationService, EmailConfirmationService>();

        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddScoped<ITenantResolver, ModeAwareTenantResolver>();
        services.AddScoped<IIssuerBuilder, IssuerBuilder>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();
        services.AddScoped<ITenantBrandingService, TenantBrandingService>();
        services.AddScoped<ITenantIconService, TenantIconService>();

        services.AddScoped<IKeyStore, KeyStore>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<IPasswordPolicyService, PasswordPolicyService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IClientStore, ClientStore>();
        services.AddScoped<IScopeResolver, ScopeResolver>();
        services.AddScoped<IScopeNameValidator, ScopeNameValidator>();
        
        // Metrics (singleton for lifetime of app)
        services.AddSingleton<IClientSecretMetrics, ClientSecretMetrics>();
        services.AddScoped<IAuthorizeService, AuthorizeService>();
        services.AddScoped<IAuthorizationCodeService, AuthorizationCodeService>();
        services.AddSingleton<IAuthorizationCodeMetadataStore, InMemoryAuthorizationCodeMetadataStore>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IConsentService, ConsentService>();
        services.AddScoped<ITokenValidator, TokenValidator>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IRevocationService, RevocationService>();
        services.AddSingleton<IClientIdGenerator, ClientIdGenerator>();
        services.AddSingleton<IClientSecretGenerator, ClientSecretGenerator>();
        services.AddSingleton<ITotpService, TotpService>();
        services.AddScoped<IOboPolicyService, OboPolicyService>();
        services.AddSingleton<IUserAgentParser, UserAgentParser>();

        // WebAuthn/FIDO2 services
        services.AddOptions<WebAuthnOptions>();
        services.AddSingleton<IFido2>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebAuthnOptions>>().Value;
            var config = new Fido2Configuration
            {
                ServerDomain = options.RelyingPartyId ?? "localhost",
                ServerName = options.RelyingPartyName ?? "MrWhoOidc",
                Origins = new HashSet<string>(options.AllowedOrigins.Length > 0 ? options.AllowedOrigins : new[] { "https://localhost" }),
                Timeout = (uint)(options.RegistrationTimeoutSeconds * 1000), // Convert seconds to milliseconds
                TimestampDriftTolerance = 5000 // 5 seconds tolerance
            };
            return new Fido2(config, null); // Using null for metadata service (can be enhanced later)
        });
        services.AddScoped<IWebAuthnService, WebAuthnService>();

        // Tenant discovery service for email-first login flow
        services.AddScoped<ITenantDiscoveryService, TenantDiscoveryService>();

        // PAR store (EF Core-backed). Swap implementation here to move to Redis later.
        services.AddScoped<IPushedAuthorizationRequestStore, EfPushedAuthorizationRequestStore>();

        // Request object (JAR) validator
        services.AddScoped<IRequestObjectValidator, RequestObjectValidator>();
        // JAR replay cache (in-memory default). TODO: replace with distributed (e.g., Redis) when configured.
        services.AddSingleton<IJarReplayCache, InMemoryJarReplayCache>();

        // Key rotation options and services
        services.AddOptions<KeyRotationOptions>();
        services.AddScoped<IKeyRotationService, KeyRotationService>();
        services.AddHostedService<KeyRotationHostedService>();
        
        // Client secret expiry monitoring
        services.AddOptions<ClientSecretExpiryMonitorOptions>();
        services.AddHostedService<ClientSecretExpiryMonitor>();

        return services;
    }
}

