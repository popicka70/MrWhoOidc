using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Repositories;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PersistenceClient = MrWhoOidc.Auth.Persistence.Client;

namespace MrWhoOidc.UnitTests.Licensing;

[TestClass]
public sealed class LicenseAnalyticsServiceTests
{
    [TestMethod]
    public async Task GetFeatureUsageAsync_GroupsMetricsByFeature()
    {
        using var db = CreateDbContext();
        var now = DateTimeOffset.UtcNow;

        var metrics = new List<FeatureUsageMetric>
        {
            new FeatureUsageMetric
            {
                FeatureName = FeatureFlags.DPoP,
                UsageCount = 3,
                FirstUsed = now.AddDays(-2),
                LastUsed = now.AddDays(-1)
            },
            new FeatureUsageMetric
            {
                FeatureName = FeatureFlags.DPoP,
                UsageCount = 2,
                FirstUsed = now.AddDays(-4),
                LastUsed = now.AddMinutes(-10)
            },
            new FeatureUsageMetric
            {
                FeatureName = FeatureFlags.AdvancedSecurity,
                UsageCount = 4,
                FirstUsed = now.AddDays(-5),
                LastUsed = now.AddDays(-3)
            }
        };

        var usageRepo = new StubFeatureUsageRepository(metrics);
        var licenseService = new StubLicenseService();
        var limitService = new StubLimitService(new Dictionary<string, long>());
        var service = CreateService(db, usageRepo, licenseService, limitService);

        var from = now.AddDays(-7);
        var to = now;

        var report = await service.GetFeatureUsageAsync(null, null, from, to);

        Assert.AreEqual(from, report.FromDate);
        Assert.AreEqual(to, report.ToDate);
    Assert.AreEqual("daily", report.AggregationPeriod);
    Assert.HasCount(2, report.Metrics);

        var top = report.Metrics[0];
        Assert.AreEqual(FeatureFlags.DPoP, top.FeatureName);
        Assert.AreEqual(5, top.UsageCount);
    Assert.AreEqual(metrics[1].FirstUsed, top.FirstUsed);
    Assert.AreEqual(metrics[1].LastUsed, top.LastUsed);

        var second = report.Metrics[1];
        Assert.AreEqual(FeatureFlags.AdvancedSecurity, second.FeatureName);
        Assert.AreEqual(4, second.UsageCount);
    }

    [TestMethod]
    public async Task GetFeatureUsageAsync_InvalidWindow_Throws()
    {
        using var db = CreateDbContext();
        var usageRepo = new StubFeatureUsageRepository(Array.Empty<FeatureUsageMetric>());
        var licenseService = new StubLicenseService();
        var limitService = new StubLimitService(new Dictionary<string, long>());
        var service = CreateService(db, usageRepo, licenseService, limitService);

        var to = DateTimeOffset.UtcNow.AddDays(-1);
        var from = to.AddHours(1);

        await AssertThrowsAsync<ArgumentException>(() => service.GetFeatureUsageAsync(null, null, from, to));
    }

    [TestMethod]
    public async Task GetUsageLimitsAsync_ReturnsCombinedLimits()
    {
        using var db = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var realm = new Realm { TenantId = tenantId, Name = "default" };

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "tenant-a",
            Name = "Tenant A",
            IssuerUri = "https://issuer",
            Status = TenantStatus.Active
        });
        db.Realms.Add(realm);

#pragma warning disable CS0618
        for (var i = 0; i < 2; i++)
        {
            db.Clients.Add(new PersistenceClient
            {
                TenantId = tenantId,
                ClientId = $"client-{i}",
                ClientSecretHash = "hash",
                RealmId = realm.Id
            });
        }
#pragma warning restore CS0618

        for (var i = 0; i < 8; i++)
        {
            db.Users.Add(new User
            {
                TenantId = tenantId,
                Username = $"user{i}",
                PasswordHash = "hash"
            });
        }

        await db.SaveChangesAsync();

        var license = new LicenseInfo(
            LicenseTier.Professional.ToTierString(),
            "Acme Corp",
            DateTimeOffset.UtcNow.AddDays(-10),
            DateTimeOffset.UtcNow.AddDays(90),
            FeatureFlags.GetFeaturesForTier(LicenseTier.Professional),
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [LicenseLimitTypes.Users] = 10,
                [LicenseLimitTypes.Clients] = 2,
                ["custom_reports"] = 99
            },
            false,
            true,
            LicenseScope.Platform,
            "platform",
            null,
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            true);

        var usageRepo = new StubFeatureUsageRepository(Array.Empty<FeatureUsageMetric>());
        var licenseService = new StubLicenseService(license);
        var limitService = new StubLimitService(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [LicenseLimitTypes.Users] = 10,
            [LicenseLimitTypes.Clients] = 2,
            [LicenseLimitTypes.Tenants] = 5,
            ["custom_reports"] = 99
        });

        var service = CreateService(db, usageRepo, licenseService, limitService);

        var report = await service.GetUsageLimitsAsync();

    Assert.AreSame(license, report.License);
    Assert.HasCount(4, report.Limits);

        var clients = report.Limits.Single(l => string.Equals(l.LimitType, LicenseLimitTypes.Clients, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(2, clients.CurrentUsage);
        Assert.AreEqual(2, clients.LimitValue);
        Assert.IsTrue(clients.IsAtLimit);
        Assert.IsFalse(clients.IsNearLimit);

        var users = report.Limits.Single(l => string.Equals(l.LimitType, LicenseLimitTypes.Users, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(8, users.CurrentUsage);
        Assert.AreEqual(10, users.LimitValue);
        Assert.IsTrue(users.IsNearLimit);
        Assert.IsFalse(users.IsAtLimit);

        var custom = report.Limits.Single(l => string.Equals(l.LimitType, "custom_reports", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, custom.CurrentUsage);
        Assert.AreEqual(99, custom.LimitValue);
    }

    [TestMethod]
    public async Task GetUsageLimitsAsync_NoLicense_Throws()
    {
        using var db = CreateDbContext();
        var usageRepo = new StubFeatureUsageRepository(Array.Empty<FeatureUsageMetric>());
        var licenseService = new StubLicenseService(null);
        var limitService = new StubLimitService(new Dictionary<string, long>());
        var service = CreateService(db, usageRepo, licenseService, limitService);

        await AssertThrowsAsync<InvalidOperationException>(() => service.GetUsageLimitsAsync());
    }

    private static AuthDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(options);
    }

    private static LicenseAnalyticsService CreateService(
        AuthDbContext db,
        IFeatureUsageRepository usageRepository,
        ILicenseService licenseService,
        ILimitService limitService,
        TimeProvider? timeProvider = null)
    {
        return new LicenseAnalyticsService(
            db,
            usageRepository,
            licenseService,
            limitService,
            NullLogger<LicenseAnalyticsService>.Instance,
            timeProvider);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            Assert.Fail($"Expected {typeof(TException).Name} but received {ex.GetType().Name}.");
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class StubFeatureUsageRepository : IFeatureUsageRepository
    {
        private readonly IReadOnlyList<FeatureUsageMetric> _metrics;

        public StubFeatureUsageRepository(IReadOnlyList<FeatureUsageMetric> metrics)
        {
            _metrics = metrics;
        }

        public Task<IReadOnlyList<FeatureUsageMetric>> GetUsageAsync(Guid? tenantId, string? featureName, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
            => Task.FromResult(_metrics);

        public Task RecordUsageAsync(string featureName, Guid? tenantId, Guid? licenseId, DateTimeOffset occurredAt, long increment, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubLicenseService : ILicenseService
    {
        private readonly LicenseInfo? _license;

        public StubLicenseService(LicenseInfo? license = null)
        {
            _license = license;
        }

        public Task<LicenseInfo?> GetCurrentLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_license);

        public Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(Guid? tenantId = null, int page = 1, int pageSize = 20, string? actionFilter = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LicenseValidationResult> InstallLicenseAsync(string licenseKey, Guid? tenantId = null, Guid? installedBy = null, string? notes = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RevokeLicenseAsync(string reason, Guid? tenantId = null, Guid? revokedBy = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LicenseValidationResult> ValidateLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubLimitService : ILimitService
    {
        private readonly Dictionary<string, long> _limits;

        public StubLimitService(IDictionary<string, long> limits)
        {
            _limits = new Dictionary<string, long>(limits, StringComparer.OrdinalIgnoreCase);
        }

        public Task<long> GetLimitAsync(string limitType, Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            if (_limits.TryGetValue(limitType, out var value))
            {
                return Task.FromResult(value);
            }

            return Task.FromResult(0L);
        }

        public Task<bool> CanAddAsync(string limitType, long currentUsage, int additionalCount = 1, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageLimitInfo>> GetUsageLimitsAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> IsWithinLimitAsync(string limitType, long currentUsage, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
