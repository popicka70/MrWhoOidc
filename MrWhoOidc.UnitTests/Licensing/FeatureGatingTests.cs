using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Licensing.Validators;
using MrWhoOidc.Auth.Licensing.Repositories;
using MrWhoOidc.WebAuth.Middleware;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Licensing;

[TestClass]
public sealed class FeatureGatingTests
{
    [TestMethod]
    public async Task FeatureService_ReturnsCommunityDefaults_WhenLicenseMissing()
    {
        var licenseService = new StubLicenseService(null);
        var service = new FeatureService(
            licenseService,
            new StubLicenseRepository(),
            new NullFeatureUsageRepository(),
            NullLogger<FeatureService>.Instance);

        var features = await service.GetEnabledFeaturesAsync();

    Assert.Contains(FeatureFlags.BasicOidc, features);
    Assert.Contains(FeatureFlags.BasicAdminUi, features);
    Assert.DoesNotContain(FeatureFlags.MultiTenancy, features);
    }

    [TestMethod]
    public async Task FeatureService_UnionsTierDefaults_WithExplicitFeatures()
    {
        var now = DateTimeOffset.UtcNow;
        var info = new LicenseInfo(
            "professional",
            "Org",
            now.AddDays(-1),
            now.AddDays(30),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom_feature" },
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            false,
            true);

        var licenseService = new StubLicenseService(info);
        var license = new License { Id = Guid.NewGuid(), Tier = info.Tier };
        var service = new FeatureService(
            licenseService,
            new StubLicenseRepository(license),
            new NullFeatureUsageRepository(),
            NullLogger<FeatureService>.Instance);

        var features = await service.GetEnabledFeaturesAsync();

    Assert.Contains("custom_feature", features);
    Assert.Contains(FeatureFlags.MultiTenancy, features);
    Assert.Contains(FeatureFlags.AdvancedSecurity, features);
    }

    [TestMethod]
    public async Task LimitService_EnforcesTenantLimit_ForCommunity()
    {
        var now = DateTimeOffset.UtcNow;
        var info = new LicenseInfo(
            "community",
            null,
            now.AddDays(-10),
            now.AddDays(10),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            false,
            true);
        var licenseService = new StubLicenseService(info);
        var limitService = new LimitService(licenseService, NullLogger<LimitService>.Instance);

        var canAddFirst = await limitService.CanAddAsync(LicenseLimitTypes.Tenants, 0, 1);
        var canAddSecond = await limitService.CanAddAsync(LicenseLimitTypes.Tenants, 1, 1);

        Assert.IsTrue(canAddFirst, "First tenant should be allowed under community tier.");
        Assert.IsFalse(canAddSecond, "Second tenant should exceed community tier limit.");
    }

    [TestMethod]
    public async Task LimitService_UsesLicenseOverrides_WhenProvided()
    {
        var now = DateTimeOffset.UtcNow;
        var overrides = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [LicenseLimitTypes.Tenants] = 10
        };
        var info = new LicenseInfo(
            "professional",
            null,
            now.AddDays(-10),
            now.AddDays(10),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            overrides,
            false,
            true);
        var licenseService = new StubLicenseService(info);
        var limitService = new LimitService(licenseService, NullLogger<LimitService>.Instance);

        var canAddTenth = await limitService.CanAddAsync(LicenseLimitTypes.Tenants, 9, 1);
        var canAddEleventh = await limitService.CanAddAsync(LicenseLimitTypes.Tenants, 10, 1);

        Assert.IsTrue(canAddTenth);
        Assert.IsFalse(canAddEleventh);
    }

    [TestMethod]
    public async Task FeatureGatingMiddleware_Blocks_WhenFeatureDisabled()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServiceProvider(new HashSet<string>());
        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequireLicenseFeatureAttribute(FeatureFlags.TokenExchange)),
            "token");
        context.SetEndpoint(endpoint);

        var middleware = new FeatureGatingMiddleware(_ => Task.FromException(new InvalidOperationException("next should not be invoked")), NullLogger<FeatureGatingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.AreEqual("application/json", context.Response.ContentType);
    }

    [TestMethod]
    public async Task FeatureGatingMiddleware_Allows_WhenFeaturePresent()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServiceProvider(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FeatureFlags.TokenExchange
        });
        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequireLicenseFeatureAttribute(FeatureFlags.TokenExchange)),
            "token");
        context.SetEndpoint(endpoint);

        var invoked = false;
        RequestDelegate next = _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        var middleware = new FeatureGatingMiddleware(next, NullLogger<FeatureGatingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.IsTrue(invoked, "Middleware should invoke next when feature is enabled.");
        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode == 0 ? StatusCodes.Status200OK : context.Response.StatusCode);
    }

    private static IServiceProvider CreateServiceProvider(IReadOnlySet<string> features)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFeatureService>(new StubFeatureService(features));
        services.AddSingleton<ITenantAccessor, NullTenantAccessor>();
        return services.BuildServiceProvider();
    }

    private sealed class StubLicenseService : ILicenseService
    {
        private readonly LicenseInfo? _license;

        public StubLicenseService(LicenseInfo? license)
        {
            _license = license;
        }

        public Task<LicenseInfo?> GetCurrentLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_license);

        public Task<LicenseValidationResult> InstallLicenseAsync(string licenseKey, Guid? tenantId = null, Guid? installedBy = null, string? notes = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<LicenseValidationResult> ValidateLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RevokeLicenseAsync(string reason, Guid? tenantId = null, Guid? revokedBy = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(Guid? tenantId = null, int page = 1, int pageSize = 20, string? actionFilter = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class StubFeatureService : IFeatureService
    {
        private readonly IReadOnlySet<string> _features;

        public StubFeatureService(IReadOnlySet<string> features)
        {
            _features = features;
        }

        public Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_features.Contains(featureName));

        public Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_features);

        public Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(Guid? tenantId = null, string? featureName = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureUsageMetric>>(Array.Empty<FeatureUsageMetric>());
    }

    private sealed class NullTenantAccessor : ITenantAccessor
    {
        public TenantContext? CurrentTenant { get; private set; }

        public void SetTenant(TenantContext? context)
        {
            CurrentTenant = context;
        }
    }

    private sealed class StubLicenseRepository : ILicenseRepository
    {
        private readonly License? _license;

        public StubLicenseRepository(License? license = null)
        {
            _license = license;
        }

        public Task<License?> GetActiveLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_license);

        public Task<License> CreateLicenseAsync(License license, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<License> UpdateLicenseAsync(License license, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeactivateLicenseAsync(Guid? tenantId, string reason, Guid? deactivatedBy = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(Guid? tenantId = null, int page = 1, int pageSize = 20, string? actionFilter = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<LicenseHistoryEntry> AddHistoryEntryAsync(LicenseHistoryEntry historyEntry, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class NullFeatureUsageRepository : IFeatureUsageRepository
    {
        public Task RecordUsageAsync(string featureName, Guid? tenantId, Guid? licenseId, DateTimeOffset occurredAt, long increment, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FeatureUsageMetric>> GetUsageAsync(Guid? tenantId, string? featureName, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureUsageMetric>>(Array.Empty<FeatureUsageMetric>());
    }
}
