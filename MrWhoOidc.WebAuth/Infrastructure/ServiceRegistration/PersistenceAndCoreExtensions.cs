using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.WebAuth.Handlers; // handlers & related interfaces live here
using Microsoft.Extensions.Caching.Memory;

namespace MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

/// <summary>
/// Phase 2 extraction: Auth persistence + core OIDC protocol handler and support service registrations.
/// This is a mechanical move from Program.cs with no behavior changes.
/// </summary>
public static class PersistenceAndCoreExtensions
{
    public static IServiceCollection AddMrWhoOidcPersistenceAndCore(this IServiceCollection services, IConfiguration configuration)
    {
        // Persistence (DbContext + seeder)
        services.AddAuthPersistence(configuration);
        services.AddScoped<ISeeder, Seeder>();

        // Core auth/domain services (moved from Program.cs via AddMrWhoOidcAuthCore)
        services.AddMrWhoOidcAuthCore();

        // Core protocol services & validators
        services.AddHttpClient(); // IdP validator + external calls (idempotent)
        services.AddScoped<IIdentityProviderValidator, IdentityProviderValidator>();
        services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();
        services.AddScoped<IParHandler, ParHandler>();
        services.AddScoped<IExternalOidcHandler, ExternalOidcHandler>();
        services.AddSingleton<IJwksCache, JwksCache>();
        services.AddScoped<IClaimMappingService, ClaimMappingService>();

        // Protocol endpoint handlers (discovery, token, etc.)
        services.AddScoped<IDiscoveryHandler, DiscoveryHandler>();
        services.AddScoped<IAuthorizeHandler, AuthorizeHandler>();
        services.AddScoped<ILogoutHandler, LogoutHandler>();
        services.AddScoped<IUpstreamLogoutService, UpstreamLogoutService>(); // uses DbContext (scoped)
        services.AddMemoryCache();
        services.AddScoped<ITokenHandler, TokenHandler>();
        services.AddScoped<ITokenGrantHandler, RefreshTokenGrantHandler>();
        services.AddScoped<ITokenGrantHandler, AuthorizationCodeGrantHandler>();
        services.AddScoped<ITokenGrantHandler, ClientCredentialsGrantHandler>();
        services.AddScoped<ITokenGrantHandler, TokenExchangeGrantHandler>();
        services.AddScoped<IUserInfoHandler, UserInfoHandler>();
        services.AddScoped<IRevocationHandler, RevocationHandler>();
        services.AddScoped<IIntrospectionHandler, IntrospectionHandler>();
        services.AddSingleton<IPublicJwksCache, PublicJwksCache>();

        return services;
    }
}
