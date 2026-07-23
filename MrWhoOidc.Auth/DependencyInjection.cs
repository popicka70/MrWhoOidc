using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;

namespace MrWhoOidc.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddMrWhoOidcAuthCore(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Needed by services that may want to access request-scoped services (e.g., CachedKeyProvider)
        services.AddHttpContextAccessor();

        // Multi-tenancy support is configured explicitly from app settings.
        if (configuration != null)
        {
            services.Configure<MultiTenancyOptions>(configuration.GetSection("MultiTenancy"));

            // Register state provider as singleton using the configured initial state.
            services.AddSingleton<MultiTenancyStateProvider>(sp =>
            {
                var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiTenancyOptions>>().Value;
                return new MultiTenancyStateProvider(options.DefaultTenantSlug, initialEnabled: options.Enabled);
            });

            services.AddSingleton<IMultiTenancyStateProvider>(sp => sp.GetRequiredService<MultiTenancyStateProvider>());
            services.AddSingleton<IMultiTenancyOptions>(sp => sp.GetRequiredService<MultiTenancyStateProvider>());

            // Register shared tenant cache options
            services.Configure<TenantCacheOptions>(configuration.GetSection("TenantCache"));

            // Register public email domain options
            services.Configure<PublicEmailDomainOptions>(configuration.GetSection("PublicEmailDomains"));
        }
        else
        {
            // Default: single-tenant mode for tests
            var provider = new MultiTenancyStateProvider("default", initialEnabled: false);
            services.AddSingleton<IMultiTenancyStateProvider>(provider);
            services.AddSingleton<IMultiTenancyOptions>(provider);

            // Default tenant cache options
            services.Configure<TenantCacheOptions>(options =>
            {
                options.L2Expiration = TimeSpan.FromHours(1);
                options.L1Expiration = TimeSpan.FromMinutes(15);
            });

            // Default public email domain options for single-tenant mode
            services.Configure<PublicEmailDomainOptions>(_ => { });
        }

        // Memory cache needed by TenantResolver
        services.AddMemoryCache();

        // HybridCache (required by services like ClientStore, UserService, TenantSettingsService, etc.)
        // In production, WebAuth will override this with a Redis-backed version, but for unit tests
        // and basic scenarios, this provides a default memory-only hybrid cache implementation.
        services.AddDataProtection();
        services.AddHybridCache();
        services.TryAddSingleton<IJwksCache, JwksCache>();
        services.TryAddSingleton<IClientJwksProvider, ClientJwksResolver>();
        services.TryAddSingleton<ISecretProtector, DataProtectionSecretProtector>();

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
        services.AddOptions<AuthOptions>()
            .Validate(o => o.TokenValidationClockSkewSeconds >= 0,
                "Auth:TokenValidationClockSkewSeconds must be non-negative.")
            .Validate(o => o.ClientAssertionClockSkewSeconds >= 0,
                "Auth:ClientAssertionClockSkewSeconds must be non-negative.")
            .Validate(o => o.CibaLoginHintTokenClockSkewSeconds >= 0,
                "Auth:CibaLoginHintTokenClockSkewSeconds must be non-negative.")
            .Validate(o => o.DpopIatLeewaySeconds >= 0,
                "Auth:DpopIatLeewaySeconds must be non-negative.");
        if (configuration != null)
        {
            services.Configure<EmailConfirmationOptions>(configuration.GetSection("EmailConfirmation"));
        }
        else
        {
            services.Configure<EmailConfirmationOptions>(_ => { });
        }

        services.TryAddSingleton<IEmailSender, NullEmailSender>();
        services.TryAddSingleton<MrWhoOidc.Auth.Observability.IAuditSink, MrWhoOidc.Auth.Observability.NoopAuditSink>();
        services.AddScoped<IEmailConfirmationService, EmailConfirmationService>();

        services.AddScoped<ITenantAccessor, TenantAccessor>();
    services.TryAddScoped<IDefaultTenantContext, DefaultTenantContext>();
        services.AddScoped<ITenantResolver, ModeAwareTenantResolver>();
        services.AddScoped<IIssuerBuilder, IssuerBuilder>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();
        services.AddScoped<ICliClientService, CliClientService>();
        services.AddScoped<ITenantBrandingService, TenantBrandingService>();
        services.AddScoped<ITenantIconService, TenantIconService>();

        services.AddScoped<IPlatformSettingsService, PlatformSettingsService>();

        // Platform-managed initial access tokens for RFC 7591 dynamic registration
        services.AddScoped<IPlatformInitialAccessTokenService, PlatformInitialAccessTokenService>();

        services.AddScoped<IKeyStore>(sp => new KeyStore(
            sp.GetRequiredService<AuthDbContext>(),
            sp.GetRequiredService<ITenantAccessor>(),
            sp.GetRequiredService<HybridCache>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeyRotationOptions>>(),
            sp.GetRequiredService<ILogger<KeyStore>>(),
            sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<ICachedKeyProvider, CachedKeyProvider>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<IPasswordPolicyService, PasswordPolicyService>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddScoped<IUserTenantMembershipService, UserTenantMembershipService>();
        services.AddScoped<ITenantEnrollmentService, TenantEnrollmentService>();
        services.AddScoped<ITenantDomainClaimService, TenantDomainClaimService>();
        services.AddScoped<IUserAccountProvisioner, UserAccountProvisioner>();
        services.AddScoped<ICurrentUserAccountResolver, CurrentUserAccountResolver>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IClientStore, ClientStore>();
        services.AddScoped<IClientAuthenticationService, ClientAuthenticationService>();
        services.AddScoped<MrWhoOidc.Auth.Services.Users.IRegistrationService, MrWhoOidc.Auth.Services.Users.RegistrationService>();
        services.AddScoped<IScopeResolver, ScopeResolver>();
        services.AddScoped<IScopeNameValidator, ScopeNameValidator>();
        services.AddScoped<ITenantsClaimService, TenantsClaimService>();

        services.AddSingleton<IClientSecretMetrics, ClientSecretMetrics>();
        services.AddSingleton<GlobalAuthMetrics>();
        services.AddScoped<IGlobalAuthenticationService, GlobalAuthenticationService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
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

        services.AddHttpClient();
        services.AddHttpClient(SectorIdentifierResolver.SafeHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHandler);
        services.AddScoped<ISectorIdentifierResolver, SectorIdentifierResolver>();
        services.AddScoped<IPairwiseSubjectService, PairwiseSubjectService>();

        services.AddOptions<WebAuthnOptions>();
        services.AddScoped<IWebAuthnService, WebAuthnService>();
        services.AddScoped<ITenantDiscoveryService, TenantDiscoveryService>();
        services.AddScoped<IPushedAuthorizationRequestStore, EfPushedAuthorizationRequestStore>();
        services.AddScoped<IRequestObjectDecryptor, RequestObjectDecryptor>();
        services.AddScoped<IRequestObjectValidator, RequestObjectValidator>();
        services.AddScoped<IAuthorizeRequestResolver, AuthorizeRequestResolver>();
        services.AddSingleton<IJarReplayCache, InMemoryJarReplayCache>();

        var keyRotationOptionsBuilder = services.AddOptions<KeyRotationOptions>();
        if (configuration != null)
        {
            keyRotationOptionsBuilder.Bind(configuration.GetSection("KeyRotation"));
        }

        keyRotationOptionsBuilder.Validate(
            options => options.RsaKeySizeBits >= 2048 && options.RsaKeySizeBits % 256 == 0,
            "KeyRotation:RsaKeySizeBits must be at least 2048 and a multiple of 256.");
        services.AddScoped<IKeyRotationService>(sp => new KeyRotationService(
            sp.GetRequiredService<AuthDbContext>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeyRotationOptions>>(),
            sp.GetRequiredService<IKeyStore>(),
            sp.GetRequiredService<ITenantAccessor>(),
            sp.GetRequiredService<ILogger<KeyRotationService>>(),
            sp.GetRequiredService<ISecretProtector>()));
        services.AddOptions<ClientSecretExpiryMonitorOptions>();
        services.AddHostedService<KeyRotationHostedService>();
        services.AddHostedService<ClientSecretExpiryMonitor>();

        // Delegated Access Grant services
        services.AddOptions<DelegationOptions>()
            .Validate(o => o.DefaultGrantLifetimeMinutes > 0,
                "DelegationOptions:DefaultGrantLifetimeMinutes must be positive.")
            .Validate(o => o.MaximumGrantLifetimeMinutes >= o.DefaultGrantLifetimeMinutes,
                "DelegationOptions:MaximumGrantLifetimeMinutes must be >= DefaultGrantLifetimeMinutes.")
            .Validate(o => o.AcceptanceWindowMinutes > 0,
                "DelegationOptions:AcceptanceWindowMinutes must be positive.")
            .Validate(o => o.AcceptanceWindowMinutes <= o.MaximumGrantLifetimeMinutes,
                "DelegationOptions:AcceptanceWindowMinutes must be <= MaximumGrantLifetimeMinutes.");
        services.AddSingleton<IDelegableCapabilityCatalog, DelegableCapabilityCatalog>();
        services.AddScoped<IDelegatedAccessAuthorizationService, DelegatedAccessAuthorizationService>();
        services.AddScoped<IDelegatedAccessGrantService>(sp => new DelegatedAccessGrantService(
            sp.GetRequiredService<AuthDbContext>(),
            sp.GetRequiredService<IDelegableCapabilityCatalog>(),
            sp.GetRequiredService<IUserTenantMembershipService>(),
            sp.GetRequiredService<IAuditSink>(),
            sp.GetRequiredService<IEmailSender>(),
            sp.GetRequiredService<IUserAccountService>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DelegationOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>(),
            sp.GetRequiredService<ILogger<DelegatedAccessGrantService>>()));

        return services;
    }

    public static IServiceCollection AddLicensingEntitlementsClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpClient<LicensingEntitlementsClient>()
            .ConfigurePrimaryHttpMessageHandler(MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHandler)
            .ConfigureHttpClient((serviceProvider, client) =>
            {
                var options = serviceProvider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<MrWhoOidc.Auth.Entitlements.Options.LicensingIntegrationOptions>>()
                    .Value;
                if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                {
                    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
                }
                client.Timeout = TimeSpan.FromSeconds(15);
            });

        services.AddTransient<ILicensingEntitlementsClient>(serviceProvider =>
            serviceProvider.GetRequiredService<LicensingEntitlementsClient>());
        services.Configure<MrWhoOidc.Auth.Entitlements.Options.LicensingIntegrationOptions>(
            configuration.GetSection("LicensingIntegration"));
        services.Replace(ServiceDescriptor.Singleton<IEntitlementsProvider, CachingEntitlementsProvider>());

        return services;
    }
}
