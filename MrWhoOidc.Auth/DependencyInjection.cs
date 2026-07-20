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
        services.AddScoped<IEmailConfirmationService, EmailConfirmationService>();

        services.AddScoped<ITenantAccessor, TenantAccessor>();
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
