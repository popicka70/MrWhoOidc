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

/// <summary>
/// Unit tests for effective license resolution in <see cref="LicenseService"/>.
/// Tests the GetEffectiveLicenseAsync method and TenantLicenseMode behavior.
/// </summary>
[TestClass]
public sealed class EffectiveLicenseResolutionTests
{
    [TestMethod]
    public async Task GetEffectiveLicenseAsync_NullTenantId_ReturnsPlatformLicense()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var platformLicense = CreatePlatformLicense();
        validator.ParsedLicense = platformLicense;
        validator.BusinessResult = LicenseValidationResult.Success(platformLicense);
        repository.SetActiveLicense(CreateLicenseEntity(platformLicense), tenantId: null);

        var service = CreateService(repository, validator);

        var result = await service.GetEffectiveLicenseAsync(null);

        Assert.IsNotNull(result);
        Assert.AreEqual(LicenseScope.Platform, result!.Scope);
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_SingleTenantMode_ReturnsPlatformLicense()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var platformLicense = CreatePlatformLicense();
        validator.ParsedLicense = platformLicense;
        validator.BusinessResult = LicenseValidationResult.Success(platformLicense);
        repository.SetActiveLicense(CreateLicenseEntity(platformLicense), tenantId: null);

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: false);
        var service = CreateService(repository, validator, multiTenancyStateProvider: multiTenancyState);

        var tenantId = Guid.NewGuid();
        var result = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.IsNotNull(result);
        Assert.AreEqual(LicenseScope.Platform, result!.Scope);
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_InheritPlatformMode_ReturnsProjectedLicense()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var tenantId = Guid.NewGuid();

        var platformLicense = CreatePlatformLicense(
            features: new HashSet<string> { "feature_a", "feature_b" });
        validator.ParsedLicense = platformLicense;
        validator.BusinessResult = LicenseValidationResult.Success(platformLicense);
        repository.SetActiveLicense(CreateLicenseEntity(platformLicense), tenantId: null);

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: true);
        var tenantLicenseModeProvider = new StubTenantLicenseModeProvider(TenantLicenseMode.InheritPlatform);
        var service = CreateService(
            repository,
            validator,
            multiTenancyStateProvider: multiTenancyState,
            tenantLicenseModeProvider: tenantLicenseModeProvider);

        var result = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.IsNotNull(result);
        Assert.AreEqual(LicenseScope.Tenant, result!.Scope);
        Assert.AreEqual(tenantId, result.LicensedTenantId);
        CollectionAssert.Contains(result.EnabledFeatures.ToList(), "feature_a");
        CollectionAssert.Contains(result.EnabledFeatures.ToList(), "feature_b");
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_InheritPlatformMode_ExcludesPlatformOnlyFeatures()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var tenantId = Guid.NewGuid();

        // multi_tenancy is a platform-only feature
        var platformLicense = CreatePlatformLicense(
            features: new HashSet<string> { "feature_a", "multi_tenancy" });
        validator.ParsedLicense = platformLicense;
        validator.BusinessResult = LicenseValidationResult.Success(platformLicense);
        repository.SetActiveLicense(CreateLicenseEntity(platformLicense), tenantId: null);

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: true);
        var tenantLicenseModeProvider = new StubTenantLicenseModeProvider(TenantLicenseMode.InheritPlatform);
        var service = CreateService(
            repository,
            validator,
            multiTenancyStateProvider: multiTenancyState,
            tenantLicenseModeProvider: tenantLicenseModeProvider);

        var result = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.IsNotNull(result);
        CollectionAssert.Contains(result!.EnabledFeatures.ToList(), "feature_a");
        CollectionAssert.DoesNotContain(result.EnabledFeatures.ToList(), "multi_tenancy");
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_SublicenseMode_ReturnsValidatedTenantLicense()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var tenantId = Guid.NewGuid();

        var platformLicense = CreatePlatformLicense(
            features: new HashSet<string> { "feature_a", "feature_b" });
        var tenantLicense = CreateTenantLicense(tenantId: tenantId,
            features: new HashSet<string> { "feature_a" });

        // Create entities with stable keys
        var platformEntity = CreateLicenseEntity(platformLicense);
        var tenantEntity = CreateLicenseEntity(tenantLicense, tenantId);

        repository.SetActiveLicense(platformEntity, tenantId: null);
        repository.SetActiveLicense(tenantEntity, tenantId: tenantId);

        // Setup validator to return tenant license when parsing tenant's key
        validator.LicenseParseMap[tenantEntity.LicenseKey] = tenantLicense;
        // For platform license parsing (used during sublicense validation)
        validator.LicenseParseMap[platformEntity.LicenseKey] = platformLicense;
        validator.ParsedLicense = platformLicense; // Default fallback
        validator.SublicenseValidationResult = LicenseValidationResult.Success(tenantLicense);

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: true);
        var tenantLicenseModeProvider = new StubTenantLicenseModeProvider(TenantLicenseMode.Sublicense);
        var service = CreateService(
            repository,
            validator,
            multiTenancyStateProvider: multiTenancyState,
            tenantLicenseModeProvider: tenantLicenseModeProvider);

        var result = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.IsNotNull(result);
        Assert.AreEqual(LicenseScope.Tenant, result!.Scope);
        Assert.AreEqual(tenantId, result.LicensedTenantId);
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_SublicenseMode_NoTenantLicense_ReturnsNull()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var tenantId = Guid.NewGuid();

        var platformLicense = CreatePlatformLicense();
        validator.ParsedLicense = platformLicense;
        validator.BusinessResult = LicenseValidationResult.Success(platformLicense);
        repository.SetActiveLicense(CreateLicenseEntity(platformLicense), tenantId: null);
        // No tenant license set - the default license won't apply in Sublicense mode since
        // GetCurrentLicenseAsync(tenantId) returns null when no license entity exists for tenant

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: true);
        var tenantLicenseModeProvider = new StubTenantLicenseModeProvider(TenantLicenseMode.Sublicense);
        // Use options without DefaultTier to prevent default license creation
        var service = CreateService(
            repository,
            validator,
            options: new LicensingOptions { DefaultTier = "", CacheExpirationMinutes = 30 },
            multiTenancyStateProvider: multiTenancyState,
            tenantLicenseModeProvider: tenantLicenseModeProvider);

        var result = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_SublicenseMode_InvalidSublicense_ReturnsNull()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var tenantId = Guid.NewGuid();

        var platformLicense = CreatePlatformLicense();
        var tenantLicense = CreateTenantLicense(tenantId: tenantId);

        validator.ParsedLicense = platformLicense;
        validator.BusinessResult = LicenseValidationResult.Success(platformLicense);
        validator.SublicenseValidationResult = LicenseValidationResult.Failure(
            "sublicense_features_exceed_platform",
            "Invalid sublicense");
        repository.SetActiveLicense(CreateLicenseEntity(platformLicense), tenantId: null);
        repository.SetActiveLicense(CreateLicenseEntity(tenantLicense, tenantId), tenantId: tenantId);

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: true);
        var tenantLicenseModeProvider = new StubTenantLicenseModeProvider(TenantLicenseMode.Sublicense);
        var service = CreateService(
            repository,
            validator,
            multiTenancyStateProvider: multiTenancyState,
            tenantLicenseModeProvider: tenantLicenseModeProvider);

        validator.LicenseParseMap[CreateLicenseEntity(tenantLicense, tenantId).LicenseKey] = tenantLicense;

        var result = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_InheritPlatformMode_NoPlatformLicense_ReturnsNull()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var tenantId = Guid.NewGuid();

        // No platform license - and disable default tier fallback
        validator.ParsedLicense = null;

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: true);
        var tenantLicenseModeProvider = new StubTenantLicenseModeProvider(TenantLicenseMode.InheritPlatform);
        var service = CreateService(
            repository,
            validator,
            options: new LicensingOptions { DefaultTier = "", CacheExpirationMinutes = 30 },
            multiTenancyStateProvider: multiTenancyState,
            tenantLicenseModeProvider: tenantLicenseModeProvider);

        var result = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetEffectiveLicenseAsync_CachesResult()
    {
        var repository = new FakeLicenseRepository();
        var validator = new FakeLicenseValidator();
        var tenantId = Guid.NewGuid();

        var platformLicense = CreatePlatformLicense();
        validator.ParsedLicense = platformLicense;
        validator.BusinessResult = LicenseValidationResult.Success(platformLicense);
        repository.SetActiveLicense(CreateLicenseEntity(platformLicense), tenantId: null);

        var multiTenancyState = new StubMultiTenancyStateProvider(isEnabled: true);
        var tenantLicenseModeProvider = new StubTenantLicenseModeProvider(TenantLicenseMode.InheritPlatform);
        var service = CreateService(
            repository,
            validator,
            multiTenancyStateProvider: multiTenancyState,
            tenantLicenseModeProvider: tenantLicenseModeProvider);

        var result1 = await service.GetEffectiveLicenseAsync(tenantId);
        var result2 = await service.GetEffectiveLicenseAsync(tenantId);

        Assert.AreSame(result1, result2);
        Assert.AreEqual(1, repository.GetActiveCalls); // Only one repository call due to caching
    }

    private static LicenseService CreateService(
        FakeLicenseRepository repository,
        FakeLicenseValidator validator,
        LicensingOptions? options = null,
        IMultiTenancyStateProvider? multiTenancyStateProvider = null,
        ITenantLicenseModeProvider? tenantLicenseModeProvider = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var licensingOptions = options ?? new LicensingOptions
        {
            DefaultTier = "community",
            CacheExpirationMinutes = 30
        };

        return new LicenseService(
            repository,
            validator,
            cache,
            Options.Create(licensingOptions),
            NullLogger<LicenseService>.Instance,
            timeProvider: null,
            multiTenancyStateProvider: multiTenancyStateProvider,
            defaultTenantContext: null,
            tenantLicenseModeProvider: tenantLicenseModeProvider);
    }

    private static LicenseInfo CreatePlatformLicense(
        IReadOnlySet<string>? features = null)
    {
        return new LicenseInfo(
            Tier: "enterprise",
            OrganizationName: "Test Org",
            ValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil: DateTimeOffset.UtcNow.AddYears(1),
            EnabledFeatures: features ?? new HashSet<string> { "default_feature" },
            Limits: new Dictionary<string, long> { ["users"] = -1 },
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
        Guid? tenantId = null,
        IReadOnlySet<string>? features = null)
    {
        return new LicenseInfo(
            Tier: "enterprise",
            OrganizationName: "Tenant Org",
            ValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil: DateTimeOffset.UtcNow.AddMonths(6),
            EnabledFeatures: features ?? new HashSet<string> { "default_feature" },
            Limits: new Dictionary<string, long> { ["users"] = 50 },
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

    private static License CreateLicenseEntity(LicenseInfo info, Guid? tenantId = null)
    {
        return new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = $"license-key-{Guid.NewGuid():N}",
            Tier = info.Tier,
            OrganizationName = info.OrganizationName,
            ValidFrom = info.ValidFrom,
            ValidUntil = info.ValidUntil,
            IsActive = true,
            TenantId = tenantId
        };
    }

    private sealed class StubMultiTenancyStateProvider : IMultiTenancyStateProvider
    {
        public StubMultiTenancyStateProvider(bool isEnabled)
        {
            IsEnabled = isEnabled;
        }

        public bool IsEnabled { get; }
        public string DefaultTenantSlug => "default";
        public void UpdateState(bool enabled) { }
    }

    private sealed class StubTenantLicenseModeProvider : ITenantLicenseModeProvider
    {
        private readonly TenantLicenseMode _mode;

        public StubTenantLicenseModeProvider(TenantLicenseMode mode)
        {
            _mode = mode;
        }

        public Task<TenantLicenseMode> GetLicenseModeAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(_mode);
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
            return Task.FromResult(license);
        }

        public Task<License> UpdateLicenseAsync(License license, CancellationToken cancellationToken = default)
        {
            UpdatedLicenses.Add(license);
            return Task.FromResult(license);
        }

        public Task<bool> DeactivateLicenseAsync(Guid? tenantId, string reason, Guid? deactivatedBy = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
            Guid? tenantId = null,
            int page = 1,
            int pageSize = 20,
            string? actionFilter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PagedResult<LicenseHistoryEntry>(new List<LicenseHistoryEntry>(), 0, page, pageSize));

        public Task<LicenseHistoryEntry> AddHistoryEntryAsync(LicenseHistoryEntry historyEntry, CancellationToken cancellationToken = default)
        {
            HistoryEntries.Add(historyEntry);
            return Task.FromResult(historyEntry);
        }
    }

    private sealed class FakeLicenseValidator : ILicenseValidator
    {
        private static readonly LicenseInfo _dummyLicense = new(
            Tier: "community",
            OrganizationName: "Dummy",
            ValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
            ValidUntil: DateTimeOffset.UtcNow.AddYears(1),
            EnabledFeatures: new HashSet<string>(),
            Limits: new Dictionary<string, long>(),
            IsExpired: false,
            IsValid: true,
            Scope: LicenseScope.Platform,
            IssuedTo: "dummy",
            LicensedTenantId: null,
            LicensedTenantSlug: null,
            DefaultTenantFeatures: new HashSet<string>(),
            HasExplicitScopeClaim: true,
            AllowedIssuers: new HashSet<string>());

        public LicenseInfo? ParsedLicense { get; set; }
        public LicenseValidationResult? BusinessResult { get; set; }
        public LicenseValidationResult? SublicenseValidationResult { get; set; }
        public Dictionary<string, LicenseInfo> LicenseParseMap { get; } = new();

        public Task<LicenseValidationResult> ValidateSignatureAsync(string licenseKey, CancellationToken cancellationToken = default)
            => Task.FromResult(ParsedLicense is not null
                ? LicenseValidationResult.Success(ParsedLicense)
                : LicenseValidationResult.InvalidFormat());

        public Task<LicenseInfo?> ParseLicenseAsync(string licenseKey, CancellationToken cancellationToken = default)
        {
            if (LicenseParseMap.TryGetValue(licenseKey, out var mapped))
            {
                return Task.FromResult<LicenseInfo?>(mapped);
            }
            return Task.FromResult(ParsedLicense);
        }

        public Task<LicenseValidationResult> ValidateBusinessRulesAsync(LicenseInfo licenseInfo, CancellationToken cancellationToken = default)
            => Task.FromResult(BusinessResult ?? LicenseValidationResult.Success(licenseInfo));

        public Task<LicenseValidationResult> ValidateSublicenseAsync(LicenseInfo sublicense, LicenseInfo parentLicense, CancellationToken cancellationToken = default)
            => Task.FromResult(SublicenseValidationResult ?? LicenseValidationResult.Success(sublicense));
    }
}
