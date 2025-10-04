using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;

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
        
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddScoped<ITenantResolver, ModeAwareTenantResolver>();

        services.AddScoped<IKeyStore, KeyStore>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IClientStore, ClientStore>();
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

        return services;
    }
}

