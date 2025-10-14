using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// Tests for tenant-specific settings overrides and isolation.
/// Verifies that tenant settings properly override platform defaults
/// and remain isolated between tenants.
/// </summary>
[TestClass]
public class SettingsOverrideTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuthDbContext _db = null!;
    private Guid _tenantAId;
    private Guid _tenantBId;
    private string _databaseName = null!;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        // Use a consistent database name so all contexts share the same in-memory database
        _databaseName = $"SettingsOverrideTests_{Guid.NewGuid()}";

        // Configure in-memory database
        services.AddDbContext<AuthDbContext>(options =>
            options.UseInMemoryDatabase(_databaseName));

        // Configure platform defaults via IConfiguration
        var configData = new Dictionary<string, string?>
        {
            ["Tokens:AccessTokenLifetimeSeconds"] = "3600",     // 60 minutes default
            ["Tokens:RefreshTokenLifetimeSeconds"] = "86400",   // 24 hours default
            ["Auth:PasswordPolicy:MinLength"] = "8",            // 8 chars default
            ["Auth:PasswordPolicy:RequireUppercase"] = "true",
            ["Auth:PasswordPolicy:RequireLowercase"] = "true",
            ["Auth:PasswordPolicy:RequireDigit"] = "true",
            ["Auth:PasswordPolicy:RequireSpecialChar"] = "false",
            ["Auth:RequireMfa"] = "false",                      // MFA disabled by default
            ["Oidc:RequirePkce"] = "false",                     // PKCE disabled by default
            ["Oidc:CorsOrigins:0"] = "https://default-origin.com"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);

        // Add multi-tenancy services
        services.AddMemoryCache();
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddSingleton<IMultiTenancyOptions>(new MultiTenancyOptions
        {
            Enabled = true,
            DefaultTenantSlug = "default"
        });
        services.AddSingleton<HybridCache, TestHybridCache>();

        // Add TenantSettingsService
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<AuthDbContext>();

        // Create two test tenants
        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();

        _db.Tenants.Add(new Tenant
        {
            Id = _tenantAId,
            Slug = "tenant-a",
            Name = "Tenant A",
            Status = TenantStatus.Active,
            IssuerUri = "https://auth.example.com/t/tenant-a"
        });

        _db.Tenants.Add(new Tenant
        {
            Id = _tenantBId,
            Slug = "tenant-b",
            Name = "Tenant B",
            Status = TenantStatus.Active,
            IssuerUri = "https://auth.example.com/t/tenant-b"
        });

        await _db.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _serviceProvider?.Dispose();
    }

    #region Token Lifetime Override Tests

    [TestMethod]
    public async Task AccessTokenLifetime_TenantOverride_UsesTenantValue()
    {
        // Arrange - Set Tenant A with custom 30-minute access token lifetime
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var customSettings = new TenantSettings
        {
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = 1800 // 30 minutes override
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, customSettings);

        // Act - Get settings for Tenant A
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        ((TenantAccessor)tenantAccessor).SetTenant(new TenantContext
        {
            TenantId = _tenantAId,
            Slug = "tenant-a",
            Name = "Tenant A"
        });

        var resolvedSettings = await settingsService.GetCurrentTenantSettingsAsync();

        // Assert
        Assert.IsNotNull(resolvedSettings.Tokens, "Token settings should not be null");
        Assert.AreEqual(1800, resolvedSettings.Tokens.AccessTokenLifetimeSeconds, "Should use tenant override value");
    }

    [TestMethod]
    public async Task AccessTokenLifetime_NoOverride_UsesPlatformDefault()
    {
        // Arrange - Tenant B has no custom settings
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();

        // Act - Get settings for Tenant B (no overrides)
        ((TenantAccessor)tenantAccessor).SetTenant(new TenantContext
        {
            TenantId = _tenantBId,
            Slug = "tenant-b",
            Name = "Tenant B"
        });

        var resolvedSettings = await settingsService.GetCurrentTenantSettingsAsync();

        // Assert
        Assert.IsNotNull(resolvedSettings.Tokens, "Token settings should not be null");
        Assert.AreEqual(3600, resolvedSettings.Tokens.AccessTokenLifetimeSeconds, "Should use platform default (60 min)");
    }

    [TestMethod]
    public async Task RefreshTokenLifetime_TenantOverride_IndependentFromOtherTenant()
    {
        // Arrange - Set different refresh token lifetimes for each tenant
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var tenantASettings = new TenantSettings
        {
            Tokens = new TokenTenantSettings
            {
                RefreshTokenLifetimeSeconds = 43200 // 12 hours for Tenant A
            }
        };

        var tenantBSettings = new TenantSettings
        {
            Tokens = new TokenTenantSettings
            {
                RefreshTokenLifetimeSeconds = 172800 // 48 hours for Tenant B
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, tenantASettings);
        await settingsService.UpdateTenantSettingsAsync(_tenantBId, tenantBSettings);

        // Act - Get settings for both tenants
        var settingsA = await settingsService.GetTenantSettingsAsync(_tenantAId);
        var settingsB = await settingsService.GetTenantSettingsAsync(_tenantBId);

        // Assert
        Assert.IsNotNull(settingsA?.Tokens, "Tenant A token settings should not be null");
        Assert.IsNotNull(settingsB?.Tokens, "Tenant B token settings should not be null");
        Assert.AreEqual(43200, settingsA.Tokens.RefreshTokenLifetimeSeconds, "Tenant A should have 12-hour refresh tokens");
        Assert.AreEqual(172800, settingsB.Tokens.RefreshTokenLifetimeSeconds, "Tenant B should have 48-hour refresh tokens");
        Assert.AreNotEqual(settingsA.Tokens.RefreshTokenLifetimeSeconds, settingsB.Tokens.RefreshTokenLifetimeSeconds,
            "Each tenant should have independent refresh token lifetimes");
    }

    #endregion

    #region Password Policy Tests

    [TestMethod]
    public async Task PasswordPolicy_TenantOverride_DifferentRequirements()
    {
        // Arrange - Set different password policies for each tenant
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var tenantASettings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                PasswordPolicy = new PasswordPolicySettings
                {
                    MinLength = 8,
                    RequireSpecialChar = false
                }
            }
        };

        var tenantBSettings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                PasswordPolicy = new PasswordPolicySettings
                {
                    MinLength = 12,
                    RequireSpecialChar = true
                }
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, tenantASettings);
        await settingsService.UpdateTenantSettingsAsync(_tenantBId, tenantBSettings);

        // Act
        var settingsA = await settingsService.GetTenantSettingsAsync(_tenantAId);
        var settingsB = await settingsService.GetTenantSettingsAsync(_tenantBId);

        // Assert
        Assert.IsNotNull(settingsA?.Auth?.PasswordPolicy, "Tenant A password policy should not be null");
        Assert.IsNotNull(settingsB?.Auth?.PasswordPolicy, "Tenant B password policy should not be null");
        
        Assert.AreEqual(8, settingsA.Auth.PasswordPolicy.MinLength, "Tenant A requires 8 chars");
        Assert.AreEqual(12, settingsB.Auth.PasswordPolicy.MinLength, "Tenant B requires 12 chars");
        
        Assert.IsFalse(settingsA.Auth.PasswordPolicy.RequireSpecialChar ?? false, "Tenant A doesn't require special char");
        Assert.IsTrue(settingsB.Auth.PasswordPolicy.RequireSpecialChar ?? false, "Tenant B requires special char");
    }

    [TestMethod]
    public async Task PasswordPolicy_PartialOverride_MergesWithPlatformDefaults()
    {
        // Arrange - Tenant A only overrides MinLength, other settings should use platform defaults
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var customSettings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                PasswordPolicy = new PasswordPolicySettings
                {
                    MinLength = 10 // Only override this
                }
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, customSettings);

        // Act
        var resolvedSettings = await settingsService.GetTenantSettingsAsync(_tenantAId);

        // Assert
        Assert.IsNotNull(resolvedSettings?.Auth?.PasswordPolicy, "Password policy should not be null");
        Assert.AreEqual(10, resolvedSettings.Auth.PasswordPolicy.MinLength, "Should use tenant override for MinLength");
        Assert.IsTrue(resolvedSettings.Auth.PasswordPolicy.RequireUppercase ?? false, "Should use platform default for RequireUppercase");
        Assert.IsTrue(resolvedSettings.Auth.PasswordPolicy.RequireLowercase ?? false, "Should use platform default for RequireLowercase");
        Assert.IsTrue(resolvedSettings.Auth.PasswordPolicy.RequireDigit ?? false, "Should use platform default for RequireDigit");
    }

    #endregion

    #region OIDC Feature Settings Tests

    [TestMethod]
    public async Task OidcSettings_PkceRequirement_DiffersByTenant()
    {
        // Arrange - Tenant A requires PKCE, Tenant B doesn't
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var tenantASettings = new TenantSettings
        {
            Oidc = new OidcTenantSettings
            {
                RequirePkce = true
            }
        };

        var tenantBSettings = new TenantSettings
        {
            Oidc = new OidcTenantSettings
            {
                RequirePkce = false
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, tenantASettings);
        await settingsService.UpdateTenantSettingsAsync(_tenantBId, tenantBSettings);

        // Act
        var settingsA = await settingsService.GetTenantSettingsAsync(_tenantAId);
        var settingsB = await settingsService.GetTenantSettingsAsync(_tenantBId);

        // Assert
        Assert.IsTrue(settingsA?.Oidc?.RequirePkce ?? false, "Tenant A should require PKCE");
        Assert.IsFalse(settingsB?.Oidc?.RequirePkce ?? false, "Tenant B should not require PKCE");
    }

    [TestMethod]
    public async Task OidcSettings_CorsOrigins_TenantSpecific()
    {
        // Arrange - Different CORS origins per tenant
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var tenantASettings = new TenantSettings
        {
            Oidc = new OidcTenantSettings
            {
                CorsOrigins = new List<string> { "https://tenant-a-app.com", "https://tenant-a-spa.com" }
            }
        };

        var tenantBSettings = new TenantSettings
        {
            Oidc = new OidcTenantSettings
            {
                CorsOrigins = new List<string> { "https://tenant-b-portal.com" }
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, tenantASettings);
        await settingsService.UpdateTenantSettingsAsync(_tenantBId, tenantBSettings);

        // Act
        var settingsA = await settingsService.GetTenantSettingsAsync(_tenantAId);
        var settingsB = await settingsService.GetTenantSettingsAsync(_tenantBId);

        // Assert
        Assert.IsNotNull(settingsA?.Oidc?.CorsOrigins, "Tenant A CORS origins should not be null");
        Assert.IsNotNull(settingsB?.Oidc?.CorsOrigins, "Tenant B CORS origins should not be null");
        
        Assert.HasCount(2, settingsA.Oidc.CorsOrigins, "Tenant A should have 2 CORS origins");
        Assert.HasCount(1, settingsB.Oidc.CorsOrigins, "Tenant B should have 1 CORS origin");
        
        Assert.Contains("https://tenant-a-app.com", settingsA.Oidc.CorsOrigins, "Tenant A should have first origin");
        Assert.Contains("https://tenant-b-portal.com", settingsB.Oidc.CorsOrigins, "Tenant B should have its origin");
        Assert.DoesNotContain("https://tenant-a-app.com", settingsB.Oidc.CorsOrigins, "Tenant B should not have Tenant A's origins");
    }

    [TestMethod]
    public async Task AuthSettings_MfaRequirement_DiffersByTenant()
    {
        // Arrange - Tenant A requires MFA, Tenant B doesn't
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var tenantASettings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                RequireMfa = true
            }
        };

        // Tenant B uses platform default (false)

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, tenantASettings);

        // Act
        var settingsA = await settingsService.GetTenantSettingsAsync(_tenantAId);
        var settingsB = await settingsService.GetTenantSettingsAsync(_tenantBId);

        // Assert
        Assert.IsTrue(settingsA?.Auth?.RequireMfa ?? false, "Tenant A should require MFA");
        Assert.IsFalse(settingsB?.Auth?.RequireMfa ?? false, "Tenant B should not require MFA (platform default)");
    }

    #endregion

    #region Settings Isolation Tests

    [TestMethod]
    public async Task SettingsIsolation_ChangingTenantA_DoesNotAffectTenantB()
    {
        // Arrange - Set initial settings for Tenant B
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        var tenantBInitialSettings = new TenantSettings
        {
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = 7200 // 2 hours
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantBId, tenantBInitialSettings);

        // Get Tenant B settings before changes
        var settingsBBefore = await settingsService.GetTenantSettingsAsync(_tenantBId);

        // Act - Update Tenant A with different settings
        var tenantASettings = new TenantSettings
        {
            Tokens = new TokenTenantSettings
            {
                AccessTokenLifetimeSeconds = 1800 // 30 minutes
            }
        };

        await settingsService.UpdateTenantSettingsAsync(_tenantAId, tenantASettings);

        // Get Tenant B settings after Tenant A changes
        var settingsBAfter = await settingsService.GetTenantSettingsAsync(_tenantBId);

        // Assert - Tenant B settings should remain unchanged
        Assert.AreEqual(
            settingsBBefore?.Tokens?.AccessTokenLifetimeSeconds,
            settingsBAfter?.Tokens?.AccessTokenLifetimeSeconds,
            "Tenant B settings should not be affected by Tenant A changes");
        
        Assert.AreEqual(7200, settingsBAfter?.Tokens?.AccessTokenLifetimeSeconds, "Tenant B should still have 2-hour tokens");

        // Verify Tenant A has different settings
        var settingsA = await settingsService.GetTenantSettingsAsync(_tenantAId);
        Assert.AreEqual(1800, settingsA?.Tokens?.AccessTokenLifetimeSeconds, "Tenant A should have 30-minute tokens");
    }

    [TestMethod]
    public async Task SettingsIsolation_DefaultSettings_AppliedWhenNoOverride()
    {
        // Arrange - Tenant A has no custom settings
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ITenantSettingsService>();

        // Act - Get platform defaults and tenant settings
        var platformDefaults = settingsService.GetPlatformDefaults();
        var tenantSettings = await settingsService.GetTenantSettingsAsync(_tenantAId);

        // Assert - Tenant should use platform defaults
        Assert.IsNotNull(tenantSettings, "Tenant settings should not be null");
        Assert.IsNotNull(platformDefaults, "Platform defaults should not be null");

        // Verify token settings match platform defaults
        Assert.AreEqual(
            platformDefaults.Tokens?.AccessTokenLifetimeSeconds,
            tenantSettings.Tokens?.AccessTokenLifetimeSeconds,
            "Should use platform default for access token lifetime");

        Assert.AreEqual(
            platformDefaults.Auth?.PasswordPolicy?.MinLength,
            tenantSettings.Auth?.PasswordPolicy?.MinLength,
            "Should use platform default for password min length");
    }

    #endregion
}
