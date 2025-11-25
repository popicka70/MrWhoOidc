using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.Licensing.Validators;

namespace MrWhoOidc.UnitTests.Licensing;

/// <summary>
/// Unit tests for sublicense validation in <see cref="LicenseValidator"/>.
/// </summary>
[TestClass]
public sealed class SublicenseValidationTests
{
    private LicenseValidator _validator = null!;

    [TestInitialize]
    public void Setup()
    {
        _validator = new LicenseValidator(
            Options.Create(new LicensingOptions()),
            NullLogger<LicenseValidator>.Instance);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_ValidSublicense_ReturnsSuccess()
    {
        var platformLicense = CreatePlatformLicense(
            features: new HashSet<string> { "feature_a", "feature_b", "feature_c" },
            limits: new Dictionary<string, long> { ["users"] = 100, ["clients"] = 50 },
            validUntil: DateTimeOffset.UtcNow.AddYears(1));

        var sublicense = CreateTenantLicense(
            features: new HashSet<string> { "feature_a", "feature_b" },
            limits: new Dictionary<string, long> { ["users"] = 50, ["clients"] = 25 },
            validUntil: DateTimeOffset.UtcNow.AddMonths(6));

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.LicenseInfo);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_SubLicenseNotTenantScoped_ReturnsFailure()
    {
        var platformLicense = CreatePlatformLicense();

        // Create a license with platform scope instead of tenant scope
        var invalidSublicense = CreatePlatformLicense(); // Platform scope

        var result = await _validator.ValidateSublicenseAsync(invalidSublicense, platformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_sublicense_scope", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_PlatformLicenseNotPlatformScoped_ReturnsFailure()
    {
        var invalidPlatformLicense = CreateTenantLicense(); // Tenant scope instead of platform

        var sublicense = CreateTenantLicense();

        var result = await _validator.ValidateSublicenseAsync(sublicense, invalidPlatformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_platform_scope", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_ExpiryExceedsPlatform_ReturnsFailure()
    {
        var platformLicense = CreatePlatformLicense(
            validUntil: DateTimeOffset.UtcNow.AddMonths(6));

        var sublicense = CreateTenantLicense(
            validUntil: DateTimeOffset.UtcNow.AddYears(1)); // Exceeds platform

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("sublicense_expiry_exceeds_platform", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_ExpiryEqualsPlatform_ReturnsSuccess()
    {
        var expiryDate = DateTimeOffset.UtcNow.AddYears(1);
        var platformLicense = CreatePlatformLicense(validUntil: expiryDate);
        var sublicense = CreateTenantLicense(validUntil: expiryDate);

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_FeatureNotInPlatform_ReturnsFailure()
    {
        var platformLicense = CreatePlatformLicense(
            features: new HashSet<string> { "feature_a", "feature_b" });

        var sublicense = CreateTenantLicense(
            features: new HashSet<string> { "feature_a", "feature_c" }); // feature_c not in platform

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("sublicense_features_exceed_platform", result.ErrorCode);
        Assert.IsTrue(result.ErrorMessage!.Contains("feature_c"));
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_AllFeaturesInPlatform_ReturnsSuccess()
    {
        var platformLicense = CreatePlatformLicense(
            features: new HashSet<string> { "feature_a", "feature_b", "feature_c" });

        var sublicense = CreateTenantLicense(
            features: new HashSet<string> { "feature_a", "feature_b" });

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_LimitExceedsPlatformLimit_ReturnsFailure()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long> { ["users"] = 100 });

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = 150 }); // Exceeds 100

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("sublicense_limit_exceeds_platform", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_LimitEqualsPlatformLimit_ReturnsSuccess()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long> { ["users"] = 100 });

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = 100 });

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_SublicenseUnlimited_PlatformLimited_ReturnsFailure()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long> { ["users"] = 100 });

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = -1 }); // Unlimited

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("sublicense_limit_exceeds_platform", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_PlatformUnlimited_SublicenseAnyValue_ReturnsSuccess()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long> { ["users"] = -1 }); // Unlimited

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = 1000 });

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_PlatformUnlimited_SublicenseUnlimited_ReturnsSuccess()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long> { ["users"] = -1 });

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = -1 });

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_SublicenseDefinesLimitNotInPlatform_Limited_ReturnsSuccess()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long>()); // No limits defined

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = 50 }); // Limited

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_SublicenseDefinesUnlimitedNotInPlatform_ReturnsFailure()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long>()); // No limits defined

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = -1 }); // Unlimited

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("sublicense_limit_exceeds_platform", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_NoFeatures_NoLimits_ReturnsSuccess()
    {
        var platformLicense = CreatePlatformLicense(
            features: new HashSet<string>(),
            limits: new Dictionary<string, long>());

        var sublicense = CreateTenantLicense(
            features: new HashSet<string>(),
            limits: new Dictionary<string, long>());

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateSublicenseAsync_MultipleLimitsViolations_ReturnsFirstFailure()
    {
        var platformLicense = CreatePlatformLicense(
            limits: new Dictionary<string, long> { ["users"] = 100, ["clients"] = 50 });

        var sublicense = CreateTenantLicense(
            limits: new Dictionary<string, long> { ["users"] = 200, ["clients"] = 100 }); // Both exceed

        var result = await _validator.ValidateSublicenseAsync(sublicense, platformLicense);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("sublicense_limit_exceeds_platform", result.ErrorCode);
    }

    private static LicenseInfo CreatePlatformLicense(
        IReadOnlySet<string>? features = null,
        IReadOnlyDictionary<string, long>? limits = null,
        DateTimeOffset? validUntil = null)
    {
        return new LicenseInfo(
            Tier: "enterprise",
            OrganizationName: "Test Org",
            ValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil: validUntil ?? DateTimeOffset.UtcNow.AddYears(1),
            EnabledFeatures: features ?? new HashSet<string> { "default_feature" },
            Limits: limits ?? new Dictionary<string, long> { ["users"] = -1 },
            IsExpired: false,
            IsValid: true,
            Scope: LicenseScope.Platform,
            IssuedTo: "platform",
            LicensedTenantId: null,
            LicensedTenantSlug: null,
            DefaultTenantFeatures: new HashSet<string>(),
            HasExplicitScopeClaim: true,
            AllowedIssuers: new HashSet<string>());
    }

    private static LicenseInfo CreateTenantLicense(
        IReadOnlySet<string>? features = null,
        IReadOnlyDictionary<string, long>? limits = null,
        DateTimeOffset? validUntil = null,
        Guid? tenantId = null)
    {
        return new LicenseInfo(
            Tier: "enterprise",
            OrganizationName: "Tenant Org",
            ValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil: validUntil ?? DateTimeOffset.UtcNow.AddMonths(6),
            EnabledFeatures: features ?? new HashSet<string> { "default_feature" },
            Limits: limits ?? new Dictionary<string, long> { ["users"] = 50 },
            IsExpired: false,
            IsValid: true,
            Scope: LicenseScope.Tenant,
            IssuedTo: "tenant-001",
            LicensedTenantId: tenantId ?? Guid.NewGuid(),
            LicensedTenantSlug: "tenant-001",
            DefaultTenantFeatures: new HashSet<string>(),
            HasExplicitScopeClaim: true,
            AllowedIssuers: new HashSet<string>(),
            ParentLicenseId: "platform-license-jti");
    }
}
