using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.Licensing.Repositories;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Validators;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.UnitTests.Licensing;

[TestClass]
public sealed class LicenseServiceTests
{
    [TestMethod]
    public async Task GetCurrentLicenseAsync_ReturnsDefaultLicense_WhenRepositoryEmpty()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var (service, cache) = CreateService(repository, validator);

        var license = await service.GetCurrentLicenseAsync();

        Assert.IsNotNull(license);
        Assert.AreEqual("community", license!.Tier);
        Assert.AreEqual(1, repository.GetActiveCalls);
        Assert.AreEqual(0, validator.ParseCalls);

        var secondCall = await service.GetCurrentLicenseAsync();
        Assert.AreSame(license, secondCall);
        Assert.AreEqual(1, repository.GetActiveCalls, "Repository should be consulted only once due to caching.");
    }

    [TestMethod]
    public async Task GetCurrentLicenseAsync_ReturnsNull_WhenValidatorCannotParseStoredLicense()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator
        {
            ParsedLicense = null
        };

        var existing = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "stored-key",
            Tier = "community",
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(10),
            IsActive = true
        };
        repository.SetActiveLicense(existing);

        var (service, _) = CreateService(repository, validator);

        var license = await service.GetCurrentLicenseAsync();

        Assert.IsNull(license);
        Assert.AreEqual(1, validator.ParseCalls);
        Assert.AreEqual(0, validator.BusinessRuleCalls, "Business validation should not run when parsing fails.");
    }

    [TestMethod]
    public async Task InstallLicenseAsync_ReplacesExistingLicense_AndInvalidatesCache()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();

        var existing = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "old-key",
            Tier = "community",
            OrganizationName = "Old Org",
            ValidFrom = DateTimeOffset.UtcNow.AddMonths(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddMonths(1),
            IsActive = true
        };
        repository.SetActiveLicense(existing);

        var newInfo = CreateLicenseInfo(tier: "enterprise", organization: "New Org");
        validator.SignatureResult = LicenseValidationResult.Success(newInfo);
        validator.ParsedLicense = newInfo;
        validator.BusinessResult = LicenseValidationResult.Success(newInfo);

        var (service, cache) = CreateService(repository, validator);
        cache.Set("license:platform", CreateLicenseInfo());

        var result = await service.InstallLicenseAsync("new-key", installedBy: Guid.NewGuid());

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("new-key", repository.CreatedLicenses.Single().LicenseKey);
        Assert.IsFalse(existing.IsActive, "Existing license should be deactivated when replaced.");
        Assert.AreEqual("Replaced by new license", existing.RevocationReason);
        CollectionAssert.AreEquivalent(new[] { "revoked", "installed" }, repository.HistoryEntries.Select(h => h.Action).ToList());
        Assert.IsFalse(cache.TryGetValue("license:platform", out _), "Cache should be cleared after installation.");
    }

    [TestMethod]
    public async Task InstallLicenseAsync_UpdatesExistingLicense_WhenKeyUnchanged()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();

        var existing = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "same-key",
            Tier = "community",
            OrganizationName = "Old Org",
            ValidFrom = DateTimeOffset.UtcNow.AddMonths(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddMonths(1),
            IsActive = true
        };
        repository.SetActiveLicense(existing);

        var updatedInfo = CreateLicenseInfo(tier: "professional", organization: "Updated Org");
        validator.SignatureResult = LicenseValidationResult.Success(updatedInfo);
        validator.ParsedLicense = updatedInfo;
        validator.BusinessResult = LicenseValidationResult.Success(updatedInfo);

        var (service, _) = CreateService(repository, validator);

        var result = await service.InstallLicenseAsync("same-key", installedBy: Guid.NewGuid(), notes: "refresh");

        Assert.IsTrue(result.IsValid);
    Assert.IsEmpty(repository.CreatedLicenses, "New license should not be created when key is unchanged.");
    Assert.HasCount(1, repository.UpdatedLicenses, "Existing license should be updated in-place.");
        Assert.AreEqual("professional", existing.Tier);
        Assert.AreEqual("Updated Org", existing.OrganizationName);
        Assert.AreEqual("updated", repository.HistoryEntries.Single().Action);
    }

    [TestMethod]
    public async Task RevokeLicenseAsync_DeactivatesLicense_AndAddsHistoryEntry()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();

        var existing = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "active-key",
            Tier = "enterprise",
            OrganizationName = "Test Org",
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(10),
            IsActive = true
        };
        repository.SetActiveLicense(existing);

        var (service, cache) = CreateService(repository, validator);
        cache.Set("license:platform", CreateLicenseInfo());

        var revoked = await service.RevokeLicenseAsync("maintenance", revokedBy: Guid.NewGuid());

        Assert.IsTrue(revoked);
        Assert.IsFalse(existing.IsActive);
        Assert.AreEqual("maintenance", existing.RevocationReason);
    Assert.HasCount(1, repository.UpdatedLicenses);
        Assert.AreEqual("revoked", repository.HistoryEntries.Single().Action);
        Assert.IsFalse(cache.TryGetValue("license:platform", out _));
    }

    [TestMethod]
    public async Task RevokeLicenseAsync_ReturnsFalse_WhenNoActiveLicenseExists()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var (service, _) = CreateService(repository, validator);

        var revoked = await service.RevokeLicenseAsync("maintenance");

        Assert.IsFalse(revoked);
    Assert.IsEmpty(repository.UpdatedLicenses);
    Assert.IsEmpty(repository.HistoryEntries);
    }

    [TestMethod]
    public async Task ValidateLicenseKeyAsync_ReturnsSignatureFailure_WhenSignatureInvalid()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator
        {
            SignatureResult = LicenseValidationResult.InvalidSignature()
        };
        var (service, _) = CreateService(repository, validator);

        var result = await service.ValidateLicenseKeyAsync("bad-key");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_signature", result.ErrorCode);
        Assert.AreEqual(1, validator.ValidateSignatureCalls);
        Assert.AreEqual(0, validator.BusinessRuleCalls);
    }

    [TestMethod]
    public async Task ValidateLicenseKeyAsync_ReturnsBusinessResult_WhenSignatureValid()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var info = CreateLicenseInfo(tier: "enterprise");
        validator.SignatureResult = LicenseValidationResult.Success(info);
        validator.BusinessResult = LicenseValidationResult.Success(info);

        var (service, _) = CreateService(repository, validator);

        var result = await service.ValidateLicenseKeyAsync("valid-key");

        Assert.IsTrue(result.IsValid);
        Assert.AreSame(info, result.LicenseInfo);
        Assert.AreEqual(1, validator.ValidateSignatureCalls);
        Assert.AreEqual(1, validator.BusinessRuleCalls);
        Assert.AreEqual(0, validator.ParseCalls, "Parse should not run when signature validation returns license info.");
    }

    [TestMethod]
    public async Task InstallLicenseAsync_ReturnsScopeMismatch_WhenPlatformLicenseTargetsTenant()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var platformInfo = CreateLicenseInfo(scope: LicenseScope.Platform);
        validator.SignatureResult = LicenseValidationResult.Success(platformInfo);
        validator.ParsedLicense = platformInfo;
        validator.BusinessResult = LicenseValidationResult.Success(platformInfo);

        var (service, _) = CreateService(repository, validator);
        var tenantId = Guid.NewGuid();

        var result = await service.InstallLicenseAsync("key", tenantId: tenantId);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("scope_mismatch", result.ErrorCode);
    }

    [TestMethod]
    public async Task InstallLicenseAsync_ReturnsTenantMismatch_WhenTargetsDifferentTenant()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var expectedTenant = Guid.NewGuid();
        var tenantLicense = CreateLicenseInfo(scope: LicenseScope.Tenant, licensedTenantId: expectedTenant, issuedTo: "expected", licensedTenantSlug: "expected");
        validator.SignatureResult = LicenseValidationResult.Success(tenantLicense);
        validator.ParsedLicense = tenantLicense;
        validator.BusinessResult = LicenseValidationResult.Success(tenantLicense);

        var (service, _) = CreateService(repository, validator);
        var otherTenant = Guid.NewGuid();

        var result = await service.InstallLicenseAsync("key", tenantId: otherTenant);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("tenant_mismatch", result.ErrorCode);
    }

    [TestMethod]
    public async Task GetCurrentLicenseAsync_UsesPlatformOverrides_ForDefaultTenant()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var defaultTenantId = Guid.NewGuid();
        var defaultFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default_feature" };
        var platformInfo = CreateLicenseInfo(
            tier: "enterprise",
            defaultTenantFeatures: defaultFeatures);
        validator.ParsedLicense = platformInfo;
        validator.BusinessResult = LicenseValidationResult.Success(platformInfo);

        repository.SetActiveLicense(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "platform",
            Tier = platformInfo.Tier,
            ValidFrom = platformInfo.ValidFrom,
            ValidUntil = platformInfo.ValidUntil,
            IsActive = true
        });

        var defaultTenantContext = new StubDefaultTenantContext(defaultTenantId, "default");
        var (service, _) = CreateService(repository, validator, defaultTenantContext: defaultTenantContext);

        var license = await service.GetCurrentLicenseAsync(defaultTenantId);

        Assert.IsNotNull(license);
        Assert.AreEqual(LicenseScope.Tenant, license!.Scope);
        Assert.AreEqual(defaultTenantId, license.LicensedTenantId);
        CollectionAssert.AreEquivalent(defaultFeatures.ToArray(), license.EnabledFeatures.ToArray());
    }

    private static (LicenseService Service, MemoryCache Cache) CreateService(
        FakeLicenseRepository repository,
        FakeLicenseValidator validator,
        LicensingOptions? options = null,
        IMultiTenancyStateProvider? multiTenancyStateProvider = null,
        IDefaultTenantContext? defaultTenantContext = null,
        TimeProvider? timeProvider = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var licensingOptions = options ?? new LicensingOptions
        {
            DefaultTier = "community",
            CacheExpirationMinutes = 30
        };
        var service = new LicenseService(
            repository,
            validator,
            cache,
            Options.Create(licensingOptions),
            NullLogger<LicenseService>.Instance,
            timeProvider,
            multiTenancyStateProvider,
            defaultTenantContext);
        return (service, cache);
    }

    private static LicenseInfo CreateLicenseInfo(
        string tier = "community",
        string? organization = "Org",
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        IReadOnlySet<string>? enabledFeatures = null,
        IReadOnlyDictionary<string, long>? limits = null,
        LicenseScope scope = LicenseScope.Platform,
        Guid? licensedTenantId = null,
        string? issuedTo = "platform",
        string? licensedTenantSlug = null,
        IReadOnlySet<string>? defaultTenantFeatures = null,
        bool hasExplicitScopeClaim = true)
    {
        var from = validFrom ?? DateTimeOffset.UtcNow.AddDays(-1);
        var until = validUntil ?? DateTimeOffset.UtcNow.AddDays(30);
        var features = enabledFeatures ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "feature" };
        var limitTable = limits ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["users"] = -1 };
        var defaultFeatures = defaultTenantFeatures ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new LicenseInfo(
            tier,
            organization,
            from,
            until,
            features,
            limitTable,
            false,
            true,
            scope,
            issuedTo,
            licensedTenantId,
            licensedTenantSlug,
            defaultFeatures,
            hasExplicitScopeClaim);
    }

    private sealed class StubDefaultTenantContext : IDefaultTenantContext
    {
        private readonly Guid? _tenantId;

        public StubDefaultTenantContext(Guid? tenantId, string slug)
        {
            _tenantId = tenantId;
            DefaultTenantSlug = slug;
        }

        public string DefaultTenantSlug { get; }

        public Task<Guid?> GetDefaultTenantIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_tenantId);
    }

    private sealed class FakeLicenseRepository : ILicenseRepository
    {
        private License? _platformLicense;
        private readonly Dictionary<Guid, License?> _tenantLicenses = new();

        public int GetActiveCalls { get; private set; }

        public List<License> CreatedLicenses { get; } = new();

        public List<License> UpdatedLicenses { get; } = new();

        public List<LicenseHistoryEntry> HistoryEntries { get; } = new();

        public void SetActiveLicense(License? license, Guid? tenantId = null)
        {
            if (tenantId is null)
            {
                _platformLicense = license;
            }
            else
            {
                _tenantLicenses[tenantId.Value] = license;
            }
        }

        public Task<License?> GetActiveLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            GetActiveCalls++;
            if (tenantId is null)
            {
                return Task.FromResult(_platformLicense);
            }

            return Task.FromResult(_tenantLicenses.TryGetValue(tenantId.Value, out var license) ? license : null);
        }

        public Task<License> CreateLicenseAsync(License license, CancellationToken cancellationToken = default)
        {
            CreatedLicenses.Add(license);
            if (license.TenantId is null)
            {
                _platformLicense = license;
            }
            else
            {
                _tenantLicenses[license.TenantId.Value] = license;
            }
            return Task.FromResult(license);
        }

        public Task<License> UpdateLicenseAsync(License license, CancellationToken cancellationToken = default)
        {
            UpdatedLicenses.Add(license);
            if (_platformLicense?.Id == license.Id)
            {
                _platformLicense = license;
            }
            else if (license.TenantId.HasValue && _tenantLicenses.TryGetValue(license.TenantId.Value, out var existing) && existing?.Id == license.Id)
            {
                _tenantLicenses[license.TenantId.Value] = license;
            }
            return Task.FromResult(license);
        }

        public Task<bool> DeactivateLicenseAsync(Guid? tenantId, string reason, Guid? deactivatedBy = null, CancellationToken cancellationToken = default)
        {
            if (tenantId is null && _platformLicense is not null)
            {
                _platformLicense.IsActive = false;
                _platformLicense.RevocationReason = reason;
                _platformLicense.UpdatedBy = deactivatedBy;
                UpdatedLicenses.Add(_platformLicense);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
            Guid? tenantId = null,
            int page = 1,
            int pageSize = 20,
            string? actionFilter = null,
            CancellationToken cancellationToken = default)
        {
            var items = HistoryEntries
                .Where(h => actionFilter is null || string.Equals(h.Action, actionFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Task.FromResult(new PagedResult<LicenseHistoryEntry>(items, items.Count, page, pageSize));
        }

        public Task<LicenseHistoryEntry> AddHistoryEntryAsync(LicenseHistoryEntry historyEntry, CancellationToken cancellationToken = default)
        {
            HistoryEntries.Add(historyEntry);
            return Task.FromResult(historyEntry);
        }
    }

    private sealed class FakeLicenseValidator : ILicenseValidator
    {
        public LicenseValidationResult SignatureResult { get; set; } = LicenseValidationResult.Success(CreateLicenseInfo());

        public LicenseInfo? ParsedLicense { get; set; } = CreateLicenseInfo();

        public LicenseValidationResult BusinessResult { get; set; } = LicenseValidationResult.Success(CreateLicenseInfo());

        public int ValidateSignatureCalls { get; private set; }

        public int ParseCalls { get; private set; }

        public int BusinessRuleCalls { get; private set; }

        public LicenseInfo? LastValidatedLicense { get; private set; }

        public Task<LicenseValidationResult> ValidateSignatureAsync(string licenseKey, CancellationToken cancellationToken = default)
        {
            ValidateSignatureCalls++;
            return Task.FromResult(SignatureResult);
        }

        public Task<LicenseInfo?> ParseLicenseAsync(string licenseKey, CancellationToken cancellationToken = default)
        {
            ParseCalls++;
            return Task.FromResult(ParsedLicense);
        }

        public Task<LicenseValidationResult> ValidateBusinessRulesAsync(LicenseInfo licenseInfo, CancellationToken cancellationToken = default)
        {
            BusinessRuleCalls++;
            LastValidatedLicense = licenseInfo;
            return Task.FromResult(BusinessResult);
        }
    }
}
