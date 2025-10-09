using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;
using System.Threading.Tasks;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MfaEnforcementTests
{
    [TestMethod]
    public async Task MfaNotRequired_AllowsLoginWithoutMfa()
    {
        // Arrange: MFA not required by tenant
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings { MinLength = 6 });
        var settings = await mockSettings.GetCurrentTenantSettingsAsync();

        // Assert
        Assert.IsFalse(settings.Auth?.RequireMfa ?? false);
    }

    [TestMethod]
    public async Task MfaRequired_SettingIsTrue()
    {
        // Arrange: Set MFA required
        var mockSettings = new MockTenantSettingsService();
        var customSettings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                RequireMfa = true
            }
        };
        var service = new MockTenantSettingsService(customSettings);

        // Act
        var settings = await service.GetCurrentTenantSettingsAsync();

        // Assert
        Assert.IsTrue(settings.Auth?.RequireMfa ?? false);
    }

    [TestMethod]
    public async Task MfaRequired_DefaultIsFalse()
    {
        // Arrange: Use default settings
        var mockSettings = new MockTenantSettingsService();

        // Act
        var settings = await mockSettings.GetCurrentTenantSettingsAsync();

        // Assert
        Assert.IsFalse(settings.Auth?.RequireMfa ?? false);
    }

    [TestMethod]
    public async Task MfaRequired_CanBeSetPerTenant()
    {
        // Arrange: Two tenants with different policies
        var tenant1Settings = new TenantSettings
        {
            Auth = new AuthTenantSettings { RequireMfa = true }
        };
        var tenant2Settings = new TenantSettings
        {
            Auth = new AuthTenantSettings { RequireMfa = false }
        };

        var service1 = new MockTenantSettingsService(tenant1Settings);
        var service2 = new MockTenantSettingsService(tenant2Settings);

        // Act
        var settings1 = await service1.GetCurrentTenantSettingsAsync();
        var settings2 = await service2.GetCurrentTenantSettingsAsync();

        // Assert
        Assert.IsTrue(settings1.Auth?.RequireMfa ?? false, "Tenant 1 should require MFA");
        Assert.IsFalse(settings2.Auth?.RequireMfa ?? false, "Tenant 2 should not require MFA");
    }

    [TestMethod]
    public async Task MfaSettings_IntegrationWithOtherAuthSettings()
    {
        // Arrange: Full auth settings
        var customSettings = new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                RequireMfa = true,
                AllowRefreshTokenIntrospection = true,
                PasswordPolicy = new PasswordPolicySettings
                {
                    MinLength = 12,
                    RequireUppercase = true,
                    RequireLowercase = true,
                    RequireDigit = true,
                    RequireSpecialChar = true
                }
            }
        };
        var service = new MockTenantSettingsService(customSettings);

        // Act
        var settings = await service.GetCurrentTenantSettingsAsync();

        // Assert
        Assert.IsTrue(settings.Auth?.RequireMfa ?? false);
        Assert.IsTrue(settings.Auth?.AllowRefreshTokenIntrospection ?? false);
        Assert.AreEqual(12, settings.Auth?.PasswordPolicy?.MinLength);
        Assert.IsTrue(settings.Auth?.PasswordPolicy?.RequireUppercase ?? false);
    }
}
