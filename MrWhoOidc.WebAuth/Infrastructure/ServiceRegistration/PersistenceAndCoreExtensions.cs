using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using MrWhoOidc.WebAuth.Security;
using MrWhoOidc.WebAuth.Handlers; // handlers & related interfaces live here
using Microsoft.Extensions.Caching.Memory;
using MrWhoOidc.WebAuth.Services;

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
        // This now includes multi-tenancy service registration
        services.AddMrWhoOidcAuthCore(configuration);
        // Diagnostic marker (used only in tests if validation flag set)
        services.AddSingleton(new AuthCoreRegistrationMarker(DateTime.UtcNow));

        // Core protocol services & validators
        services.AddHttpClient(); // IdP validator + external calls (idempotent)
        services.AddScoped<IIdentityProviderValidator, IdentityProviderValidator>();
        services.AddScoped<IClientAssertionValidator, ClientAssertionValidator>();
        services.AddScoped<IParHandler, ParHandler>();
        services.AddExternalOidcHandler(); // External OIDC handler with refactored services
        services.AddSingleton<IJwksCache, JwksCache>();
        services.AddScoped<IClaimMappingService, ClaimMappingService>();

        // Protocol endpoint handlers (discovery, token, etc.)
        services.AddScoped<IDiscoveryHandler, DiscoveryHandler>();
        services.AddScoped<IAuthorizeHandler, AuthorizeHandler>();
        
        // Logout services (refactored)
        services.AddScoped<Handlers.Logout.ILogoutHandler, Handlers.Logout.LogoutHandler>();
        services.AddScoped<Handlers.Logout.LocalLogoutHandler>();
        services.AddScoped<Handlers.Logout.FederatedLogoutEntryHandler>();
        services.AddScoped<Handlers.Logout.FederatedCallbackHandler>();
        services.AddScoped<Handlers.Logout.EndSessionHandler>();
        services.AddScoped<Handlers.Logout.LogoutRedirectResolver>();
        services.AddScoped<Handlers.Logout.FrontChannelLogoutNotifier>();
        services.AddScoped<Handlers.Logout.BackChannelLogoutEnqueuer>();
        services.AddScoped<Handlers.Logout.PostLogoutRedirectValidator>();
        services.AddScoped<Handlers.Logout.LogoutTokenBuilder>();
        
        services.AddScoped<IUpstreamLogoutService, UpstreamLogoutService>(); // uses DbContext (scoped)
        services.AddMemoryCache();
        services.AddScoped<ITokenHandler, TokenHandler>();
        services.AddScoped<ITokenGrantHandler, RefreshTokenGrantHandler>();
        services.AddScoped<ITokenGrantHandler, AuthorizationCodeGrantHandler>();
        services.AddScoped<ITokenGrantHandler, ClientCredentialsGrantHandler>();
        services.AddScoped<ITokenGrantHandler, TokenExchangeGrantHandler>();
        services.AddScoped<IUserInfoHandler, UserInfoHandler>();
        services.AddScoped<IRevocationHandler, RevocationHandler>();
        
        // Introspection services
        services.AddScoped<IIntrospectionHandler, Handlers.Introspection.IntrospectionHandler>();
        services.AddScoped<Handlers.Introspection.ClientAuthenticator>();
        services.AddScoped<Handlers.Introspection.DPoPValidator>();
        services.AddScoped<Handlers.Introspection.AudiencePolicy>();
        services.AddScoped<Handlers.Introspection.ResponseShaper>();
        services.AddScoped<Handlers.Introspection.JwtTokenIntrospector>();
        services.AddScoped<Handlers.Introspection.OpaqueTokenIntrospector>();
        services.AddScoped<Handlers.Introspection.RefreshTokenIntrospector>();
        
        services.AddSingleton<IPublicJwksCache, PublicJwksCache>();

        // QR login services
        services.AddScoped<IQrLoginService, QrLoginService>();
        services.AddScoped<IQrCodeGenerator, QrCodeGenerator>();
        services.AddScoped<IQrLoginHandler, QrLoginHandler>();

        // Hosted validator (optional – only throws if Testing:ValidateAuthCore=true)
        services.AddHostedService<AuthCoreValidationHostedService>();
        return services;
    }
}

internal sealed record AuthCoreRegistrationMarker(DateTime When);

internal sealed class AuthCoreValidationHostedService(
    IServiceProvider sp,
    ILogger<AuthCoreValidationHostedService> logger,
    IConfiguration config) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(config["Testing:ValidateAuthCore"], "true", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        using var scope = sp.CreateScope();
        var required = new[]
        {
            typeof(IKeyStore),
            typeof(IPasswordHasher),
            typeof(ITokenService),
            typeof(ITokenValidator)
        };
        var missing = required.Where(t => scope.ServiceProvider.GetService(t) is null).Select(t => t.Name).ToList();
        if (missing.Count > 0)
        {
            var msg = "AuthCoreValidationHostedService detected missing: " + string.Join(", ", missing);
            logger.LogError(msg);
            throw new InvalidOperationException(msg);
        }
        logger.LogInformation("AuthCoreValidationHostedService: all core services present.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
