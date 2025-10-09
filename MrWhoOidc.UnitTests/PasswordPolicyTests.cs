using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;
using System.Threading.Tasks;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class PasswordPolicyTests
{
    [TestMethod]
    public async Task ValidatePassword_WithMinimalPolicy_Accepts6CharPassword()
    {
        // Arrange: default policy (6 chars min, no requirements)
        var mockSettings = new MockTenantSettingsService();
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("abc123");

        // Assert
        Assert.IsTrue(result.IsValid);
        Assert.IsEmpty(result.Errors);
    }

    [TestMethod]
    public async Task ValidatePassword_WithMinimalPolicy_RejectsTooShort()
    {
        // Arrange
        var mockSettings = new MockTenantSettingsService();
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("abc12");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.HasCount(1, result.Errors);
        Assert.Contains("at least 6 characters", result.Errors[0]);
    }

    [TestMethod]
    public async Task ValidatePassword_WithUppercaseRequired_RejectsNoUppercase()
    {
        // Arrange: require uppercase
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 6,
            RequireUppercase = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("abc123");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.HasCount(1, result.Errors);
        Assert.Contains("uppercase", result.Errors[0]);
    }

    [TestMethod]
    public async Task ValidatePassword_WithUppercaseRequired_AcceptsWithUppercase()
    {
        // Arrange
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 6,
            RequireUppercase = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("Abc123");

        // Assert
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidatePassword_WithLowercaseRequired_RejectsNoLowercase()
    {
        // Arrange
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 6,
            RequireLowercase = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("ABC123");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.Contains("lowercase", result.Errors[0]);
    }

    [TestMethod]
    public async Task ValidatePassword_WithDigitRequired_RejectsNoDigit()
    {
        // Arrange
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 6,
            RequireDigit = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("abcdef");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.Contains("digit", result.Errors[0]);
    }

    [TestMethod]
    public async Task ValidatePassword_WithSpecialCharRequired_RejectsNoSpecialChar()
    {
        // Arrange
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 6,
            RequireSpecialChar = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("Abc123");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.Contains("special character", result.Errors[0]);
    }

    [TestMethod]
    public async Task ValidatePassword_WithSpecialCharRequired_AcceptsWithSpecialChar()
    {
        // Arrange
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 6,
            RequireSpecialChar = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("Abc123!");

        // Assert
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidatePassword_WithAllRequirements_AcceptsCompliantPassword()
    {
        // Arrange: strict policy
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 12,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireDigit = true,
            RequireSpecialChar = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("MyP@ssw0rd123!");

        // Assert
        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidatePassword_WithAllRequirements_RejectsMultipleViolations()
    {
        // Arrange: strict policy
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 12,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireDigit = true,
            RequireSpecialChar = true
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act: too short, no uppercase, no digit, no special char
        var result = await service.ValidatePasswordAsync("password");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.HasCount(4, result.Errors); // Should have 4 errors
    }

    [TestMethod]
    public async Task ValidatePassword_WithEmptyPassword_RejectsWithError()
    {
        // Arrange
        var mockSettings = new MockTenantSettingsService();
        var service = new PasswordPolicyService(mockSettings);

        // Act
        var result = await service.ValidatePasswordAsync("");

        // Assert
        Assert.IsFalse(result.IsValid);
        Assert.Contains("cannot be empty", result.Errors[0]);
    }

    [TestMethod]
    public async Task ValidatePassword_WithCustomMinLength_EnforcesCorrectLength()
    {
        // Arrange: require 10 chars
        var mockSettings = new MockTenantSettingsService();
        mockSettings.SetPasswordPolicy(new PasswordPolicySettings
        {
            MinLength = 10
        });
        var service = new PasswordPolicyService(mockSettings);

        // Act: 9 chars
        var result1 = await service.ValidatePasswordAsync("abcdefghi");
        // Act: 10 chars
        var result2 = await service.ValidatePasswordAsync("abcdefghij");

        // Assert
        Assert.IsFalse(result1.IsValid);
        Assert.Contains("at least 10 characters", result1.Errors[0]);
        Assert.IsTrue(result2.IsValid);
    }
}
