using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Seeding;

/// <summary>
/// Third 2026-10-04 review: seeding a tenant for admin@customer.example derived the username "admin", which
/// matched the platform admin's account; that account was then made the new tenant's admin.
/// </summary>
[TestClass]
public sealed class TenantSeedingAccountConflictTests
{
    private static TenantSeedingService CreateService(AuthDbContext db)
    {
        var tenantService = new Mock<ITenantService>();
        tenantService.Setup(t => t.CanProvisionTenantAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var issuerBuilder = new Mock<IIssuerBuilder>();
        issuerBuilder.Setup(i => i.BuildIssuer(It.IsAny<string>(), It.IsAny<string>())).Returns("https://localhost/t/slug");

        return new TenantSeedingService(
            db,
            Mock.Of<IPasswordHasher>(),
            tenantService.Object,
            NullLogger<TenantSeedingService>.Instance,
            new UserAccountProvisioner(db, Options.Create(new UserAccountFeatureOptions { UserAccountDecouplingEnabled = true }), NullLogger<UserAccountProvisioner>.Instance),
            Options.Create(new OidcOptions { SampleWebClientBaseUrl = "http://localhost:5000" }),
            issuerBuilder.Object,
            Mock.Of<IHttpContextAccessor>());
    }

    private static async Task<UserAccount> SeedPlatformAdminAsync(AuthDbContext db)
    {
        var account = new UserAccount { Username = "admin", Email = "admin@platform.example", NormalizedEmail = "admin@platform.example", PasswordHash = "hash" };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    [TestMethod]
    public async Task AdminEmailLocalPartEqualToPlatformAdminUsername_DoesNotAdoptPlatformAdmin()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var platformAdmin = await SeedPlatformAdminAsync(db);

        var result = await CreateService(db).SeedSampleTenantAsync("customer", "Customer", "admin@customer.example");

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsFalse(db.UserTenantMemberships.Any(m => m.UserAccountId == platformAdmin.Id),
            "the platform admin must not become a member of the seeded tenant");
        var seededAdmin = db.Users.Single(u => u.Email == "admin@customer.example");
        Assert.AreEqual("admin@customer.example", seededAdmin.Username);
        Assert.AreEqual(seededAdmin.Id, seededAdmin.UserAccountId, "the seeded admin gets an account of their own");
    }

    [TestMethod]
    public async Task AdminEmailOwnedByAnExistingAccount_FailsBeforeCreatingTheTenant()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        await SeedPlatformAdminAsync(db);

        var result = await CreateService(db).SeedSampleTenantAsync("customer", "Customer", "admin@platform.example");

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse(db.Tenants.Any(t => t.Slug == "customer"));
    }
}
