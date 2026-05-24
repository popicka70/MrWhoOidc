using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public sealed class TenantDomainClaimServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task CreateClaimAsync_NormalizesDomainAndResolvesAutoJoinMatch()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db, "acme", "Acme");
        var service = CreateService(db);

        var result = await service.CreateClaimAsync(
            tenant.Id,
            "  Example.COM. ",
            TenantDomainEnrollmentMode.AutoJoin,
            createdByUserId: null,
            createdByUsername: "admin@example.com");

        Assert.AreEqual("example.com", result.Claim.Domain);
        Assert.AreEqual("example.com", result.Claim.NormalizedDomain);
        Assert.AreEqual(TenantDomainClaimStatus.Verified, result.Claim.Status);

        var match = await service.ResolveAutoJoinClaimAsync("New.User@EXAMPLE.com");

        Assert.IsNotNull(match);
        Assert.AreEqual(tenant.Id, match.TenantId);
        Assert.AreEqual("acme", match.TenantSlug);
    }

    [TestMethod]
    public async Task CreateClaimAsync_WhenDomainAlreadyClaimedByAnotherTenant_Throws()
    {
        using var db = CreateDb();
        var firstTenant = await SeedTenantAsync(db, "acme", "Acme");
        var secondTenant = await SeedTenantAsync(db, "contoso", "Contoso");
        var service = CreateService(db);

        await service.CreateClaimAsync(firstTenant.Id, "example.com", TenantDomainEnrollmentMode.AutoJoin, null, null);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.CreateClaimAsync(secondTenant.Id, "EXAMPLE.com", TenantDomainEnrollmentMode.AutoJoin, null, null));
    }

    [TestMethod]
    public async Task CreateClaimAsync_WhenPublicEmailProviderDomain_Throws()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db, "acme", "Acme");
        var service = CreateService(db);

        await Assert.ThrowsExactlyAsync<ValidationException>(() =>
            service.CreateClaimAsync(tenant.Id, "gmail.com", TenantDomainEnrollmentMode.AutoJoin, null, null));
    }

    [TestMethod]
    public async Task RevokeClaimAsync_RemovesClaimFromAutoJoinResolution()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db, "acme", "Acme");
        var service = CreateService(db);
        var result = await service.CreateClaimAsync(tenant.Id, "example.com", TenantDomainEnrollmentMode.AutoJoin, null, null);

        var revoked = await service.RevokeClaimAsync(tenant.Id, result.Claim.Id, revokedByUserId: null, reason: "test");
        var match = await service.ResolveAutoJoinClaimAsync("user@example.com");

        Assert.IsTrue(revoked);
        Assert.IsNull(match);
    }

    [TestMethod]
    public async Task TenantDiscovery_IncludesVerifiedAutoJoinDomainClaimForNewEmail()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db, "acme", "Acme");
        var domainClaims = CreateService(db);
        await domainClaims.CreateClaimAsync(tenant.Id, "example.com", TenantDomainEnrollmentMode.AutoJoin, null, null);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var multiTenancy = new MultiTenancyStateProvider("default", initialEnabled: true);
        var discovery = new TenantDiscoveryService(
            db,
            cache,
            NullLogger<TenantDiscoveryService>.Instance,
            multiTenancy,
            domainClaims);

        var tenants = await discovery.FindTenantsByEmailAsync("new.user@example.com");

        Assert.AreEqual(1, tenants.Count);
        Assert.AreEqual(tenant.Id, tenants[0].TenantId);
        Assert.AreEqual("/t/acme/login", tenants[0].LoginUrl);
    }

    private static ITenantDomainClaimService CreateService(AuthDbContext db)
        => new TenantDomainClaimService(db, NullLogger<TenantDomainClaimService>.Instance);

    private static async Task<Tenant> SeedTenantAsync(AuthDbContext db, string slug, string name)
    {
        var tenant = new Tenant
        {
            Slug = slug,
            Name = name,
            IssuerUri = $"https://localhost:8443/t/{slug}",
            Status = TenantStatus.Active
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }
}