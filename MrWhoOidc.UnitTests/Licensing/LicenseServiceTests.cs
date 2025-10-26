using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.Licensing.Repositories;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Validators;

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

    private static (LicenseService Service, MemoryCache Cache) CreateService(
        FakeLicenseRepository repository,
        FakeLicenseValidator validator,
        LicensingOptions? options = null)
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
            NullLogger<LicenseService>.Instance);
        return (service, cache);
    }

    private static LicenseInfo CreateLicenseInfo(
        string tier = "community",
        string? organization = "Org",
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null)
    {
        var from = validFrom ?? DateTimeOffset.UtcNow.AddDays(-1);
        var until = validUntil ?? DateTimeOffset.UtcNow.AddDays(30);
        return new LicenseInfo(
            tier,
            organization,
            from,
            until,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "feature" },
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["users"] = -1 },
            false,
            true);
    }

    private sealed class FakeLicenseRepository : ILicenseRepository
    {
        private License? _platformLicense;

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
                throw new NotSupportedException("Tenant-specific licensing not implemented for this test stub.");
            }
        }

        public Task<License?> GetActiveLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            GetActiveCalls++;
            if (tenantId is null)
            {
                return Task.FromResult(_platformLicense);
            }

            throw new NotSupportedException("Tenant-specific licensing not implemented for this test stub.");
        }

        public Task<License> CreateLicenseAsync(License license, CancellationToken cancellationToken = default)
        {
            CreatedLicenses.Add(license);
            _platformLicense = license;
            return Task.FromResult(license);
        }

        public Task<License> UpdateLicenseAsync(License license, CancellationToken cancellationToken = default)
        {
            UpdatedLicenses.Add(license);
            if (_platformLicense?.Id == license.Id)
            {
                _platformLicense = license;
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
