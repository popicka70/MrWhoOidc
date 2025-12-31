using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Services.Authorization;
using Fido2NetLib;

namespace MrWhoOidc.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddMrWhoOidcAuthCore(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Needed by services that may want to access request-scoped services (e.g., CachedKeyProvider)
        services.AddHttpContextAccessor();

        // Multi-tenancy support
        // Note: Multi-tenancy Enabled state is controlled by license, not configuration.
        // Configuration only provides DefaultTenantSlug.
        if (configuration != null)
        {
            services.Configure<MultiTenancyOptions>(configuration.GetSection("MultiTenancy"));
            
            // Register state provider as singleton, always starting with Enabled=false.
            // The MultiTenancyStateInitializer will update this from the license at startup.
            services.AddSingleton<MultiTenancyStateProvider>(sp => {
                var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiTenancyOptions>>().Value;
                // Always start with Enabled=false; license will set the real value
                return new MultiTenancyStateProvider(options.DefaultTenantSlug, initialEnabled: false);
            });
            
            services.AddSingleton<IMultiTenancyStateProvider>(sp => sp.GetRequiredService<MultiTenancyStateProvider>());
            services.AddSingleton<IMultiTenancyOptions>(sp => sp.GetRequiredService<MultiTenancyStateProvider>());
            
            // Initialize state from license at startup
            services.AddHostedService<MultiTenancyStateInitializer>();
        }
        else
        {
            // Default: single-tenant mode for tests
            var provider = new MultiTenancyStateProvider("default", initialEnabled: false);
            services.AddSingleton<IMultiTenancyStateProvider>(provider);
            services.AddSingleton<IMultiTenancyOptions>(provider);
        }

        // Memory cache needed by TenantResolver
        services.AddMemoryCache();

        // HybridCache (required by services like ClientStore, UserService, TenantSettingsService, etc.)
        // In production, WebAuth will override this with a Redis-backed version, but for unit tests
        // and basic scenarios, this provides a default memory-only hybrid cache implementation.
        services.AddHybridCache();

        services.AddOptions<UserAccountFeatureOptions>();
        if (configuration != null)
        {
            services.Configure<UserAccountFeatureOptions>(configuration.GetSection("Features:UserAccount"));
        }
        else
        {
            services.Configure<UserAccountFeatureOptions>(_ => { });
        }

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
        services.AddSingleton<ICachedKeyProvider, CachedKeyProvider>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<IPasswordPolicyService, PasswordPolicyService>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddScoped<IUserTenantMembershipService, UserTenantMembershipService>();
        services.AddScoped<IUserAccountProvisioner, UserAccountProvisioner>();
        services.AddScoped<ICurrentUserAccountResolver, CurrentUserAccountResolver>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IClientStore, ClientStore>();
        services.AddScoped<IClientAuthenticationService, ClientAuthenticationService>();
        services.AddScoped<MrWhoOidc.Auth.Services.Users.IRegistrationService, MrWhoOidc.Auth.Services.Users.RegistrationService>();
        services.AddScoped<IScopeResolver, ScopeResolver>();
        services.AddScoped<IScopeNameValidator, ScopeNameValidator>();
        services.AddScoped<ITenantsClaimService, TenantsClaimService>();
        
        // Metrics (singleton for lifetime of app)
        services.AddSingleton<IClientSecretMetrics, ClientSecretMetrics>();
        services.AddSingleton<GlobalAuthMetrics>();
        
        // Global authentication service
        services.AddScoped<IGlobalAuthenticationService, GlobalAuthenticationService>();
        
        // Password reset service
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        
        // Password migration service (for migrating per-tenant to global credentials)
    #pragma warning disable CS0618
        services.AddScoped<IPasswordMigrationService, PasswordMigrationService>();
    #pragma warning restore CS0618
        
        services.AddScoped<IAuthorizeService, AuthorizeService>();
        services.AddScoped<IAuthorizeRequestValidator, AuthorizeRequestValidator>();
        services.AddScoped<IConsentProcessor, ConsentProcessor>();
        services.AddScoped<IProviderSelectionService, ProviderSelectionService>();
        services.AddScoped<IUserClientAssignmentService, UserClientAssignmentService>();
        services.AddScoped<IAuthorizationCodeService, AuthorizationCodeService>();
        services.AddSingleton<IAuthorizationCodeMetadataStore, InMemoryAuthorizationCodeMetadataStore>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IJarmService, JarmService>();
        services.AddScoped<ITokenExchangeService, TokenExchangeService>();
        services.AddScoped<MrWhoOidc.Auth.Services.Token.ILogoutTokenService, MrWhoOidc.Auth.Services.Token.LogoutTokenService>();
        services.AddScoped<IAuthorizationCodeExchanger, AuthorizationCodeExchanger>();
        services.AddScoped<IRefreshTokenExchanger, RefreshTokenExchanger>();
        services.AddScoped<IClientCredentialsTokenFactory, ClientCredentialsTokenFactory>();
        services.AddScoped<IDeviceCodeTokenFactory, DeviceCodeTokenFactory>();
        services.AddScoped<IAccessTokenClaimBuilder, AccessTokenClaimBuilder>();
        services.AddSingleton<ITokenLifetimeResolver, TokenLifetimeResolver>();
        services.AddSingleton<IRoleClaimBuilder, RoleClaimBuilder>();
        services.AddSingleton<IOpaqueTokenPolicy, OpaqueTokenPolicy>();
        services.AddSingleton<IMtlsThumbprintResolver, MtlsThumbprintResolver>();
        services.TryAddSingleton<IEntitlementsProvider, NoopEntitlementsProvider>();
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
        services.AddScoped<IAuthorizeRequestResolver, AuthorizeRequestResolver>();
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

