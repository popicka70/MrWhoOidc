using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.Licensing.Validators;

namespace MrWhoOidc.UnitTests.Licensing;

[TestClass]
public sealed class LicenseValidatorBusinessRulesTests
{
    [TestMethod]
    public async Task ValidateBusinessRulesAsync_PerpetualLicense_DoesNotOverflow()
    {
        var now = new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var validator = new LicenseValidator(
            Options.Create(new LicensingOptions
            {
                GracePeriodDays = 7,
                StrictValidation = true
            }),
            NullLogger<LicenseValidator>.Instance,
            new FixedTimeProvider(now));

        var license = CreateLicenseInfo(validFrom: now.AddDays(-1), validUntil: DateTimeOffset.MaxValue);

        var result = await validator.ValidateBusinessRulesAsync(license);

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.LicenseInfo);
        Assert.IsFalse(result.LicenseInfo!.IsExpired);
        Assert.IsTrue(result.LicenseInfo.IsValid);
    }

    [TestMethod]
    public async Task ValidateBusinessRulesAsync_ExpiredLicense_WithHugeGracePeriod_DoesNotOverflow()
    {
        var now = new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var validator = new LicenseValidator(
            Options.Create(new LicensingOptions
            {
                GracePeriodDays = int.MaxValue,
                StrictValidation = false
            }),
            NullLogger<LicenseValidator>.Instance,
            new FixedTimeProvider(now));

        var validUntil = now.AddDays(-1);
        var license = CreateLicenseInfo(validFrom: now.AddDays(-10), validUntil: validUntil);

        var result = await validator.ValidateBusinessRulesAsync(license);

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.LicenseInfo);
        Assert.IsTrue(result.LicenseInfo!.IsExpired);
        Assert.IsTrue(result.LicenseInfo.IsValid, "Expired licenses should be treated as valid inside grace window when strict validation is disabled.");
    }

    private static LicenseInfo CreateLicenseInfo(DateTimeOffset validFrom, DateTimeOffset validUntil) => new(
        Tier: "enterprise",
        OrganizationName: "Test",
        ValidFrom: validFrom,
        ValidUntil: validUntil,
        EnabledFeatures: new HashSet<string>(),
        Limits: new Dictionary<string, long>(),
        IsExpired: false,
        IsValid: true,
        Scope: LicenseScope.Platform,
        IssuedTo: "test",
        LicensedTenantId: null,
        LicensedTenantSlug: null,
        DefaultTenantFeatures: new HashSet<string>(),
        HasExplicitScopeClaim: true,
        AllowedIssuers: new HashSet<string>());

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long GetTimestamp() => _utcNow.UtcDateTime.Ticks;
    }
}
