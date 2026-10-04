using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// K1/K2 of the 2026-10-04 post-Phase-0 review: per-tenant users are linked to their global account by
/// username/email, so a tenant-side write of another account's identifier handed that account to the tenant.
/// </summary>
[TestClass]
public sealed class AccountLinkingTakeoverTests
{
    private static readonly Guid HomeTenant = Guid.NewGuid();
    private static readonly Guid AttackerTenant = Guid.NewGuid();

    private static UserAccountProvisioner CreateProvisioner(AuthDbContext db) => new(
        db,
        Options.Create(new UserAccountFeatureOptions { UserAccountDecouplingEnabled = true }),
        NullLogger<UserAccountProvisioner>.Instance);

    private static async Task<UserAccount> SeedVictimAsync(AuthDbContext db)
    {
        var home = new User { TenantId = HomeTenant, Username = "root", Email = "root@corp.example", NormalizedEmail = "root@corp.example" };
        var account = new UserAccount
        {
            Id = home.Id,
            Username = "root",
            Email = "root@corp.example",
            NormalizedEmail = "root@corp.example",
            EmailVerified = true,
            TotpEnabled = true,
            TotpSecret = "victim-totp",
            PasswordHash = "hash",
        };
        db.Users.Add(home);
        db.UserAccounts.Add(account);
        db.UserTenantMemberships.Add(new UserTenantMembership { UserAccountId = account.Id, TenantId = HomeTenant });
        await db.SaveChangesAsync();
        return account;
    }

    [TestMethod]
    public async Task EnsureAsync_ForeignTenantUserWithVictimUsername_DoesNotRewriteVictimAccount()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        var squatter = new User { TenantId = AttackerTenant, Username = "root", Email = "attacker@evil.example", NormalizedEmail = "attacker@evil.example" };
        db.Users.Add(squatter);
        await db.SaveChangesAsync();

        await CreateProvisioner(db).EnsureAsync(squatter, AttackerTenant, null, isTenantAdmin: false);

        var account = db.UserAccounts.Single(a => a.Id == victim.Id);
        Assert.AreEqual("root@corp.example", account.Email, "a foreign tenant must not change the account's email");
        Assert.IsTrue(account.TotpEnabled, "a foreign tenant must not switch off the account's TOTP");
        Assert.AreEqual("victim-totp", account.TotpSecret);
    }

    [TestMethod]
    public async Task FindConflictingAccount_NewUserWithExistingUsernameOrEmail_ReportsConflict()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        var provisioner = CreateProvisioner(db);

        Assert.AreEqual(victim.Id, (await provisioner.FindConflictingAccountAsync(null, "root", "new@evil.example"))?.Id);
        Assert.AreEqual(victim.Id, (await provisioner.FindConflictingAccountAsync(null, "fresh", "ROOT@corp.example"))?.Id);
        Assert.IsNull(await provisioner.FindConflictingAccountAsync(null, "fresh", "fresh@example.com"));
    }

    [TestMethod]
    public async Task FindConflictingAccount_EmailChangeToAnotherAccountsEmail_ReportsConflict()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        var mallory = new User { TenantId = AttackerTenant, Username = "mallory", Email = "mallory@evil.example", NormalizedEmail = "mallory@evil.example" };
        db.Users.Add(mallory);
        db.UserAccounts.Add(new UserAccount { Id = mallory.Id, Username = "mallory", Email = mallory.Email, NormalizedEmail = mallory.NormalizedEmail, PasswordHash = "h" });
        await db.SaveChangesAsync();

        var conflict = await CreateProvisioner(db).FindConflictingAccountAsync(mallory, null, "root@corp.example");

        Assert.AreEqual(victim.Id, conflict?.Id);
    }

    [TestMethod]
    public async Task FindConflictingAccount_SecondaryTenantUserOfSameAccount_IsNotAConflict()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        await SeedVictimAsync(db);
        // The same person's user in another tenant: fresh Id, renamed username, linked by email.
        var secondary = new User { TenantId = AttackerTenant, Username = "root-2", Email = "root@corp.example", NormalizedEmail = "root@corp.example" };
        db.Users.Add(secondary);
        await db.SaveChangesAsync();

        var provisioner = CreateProvisioner(db);

        Assert.IsNull(await provisioner.FindConflictingAccountAsync(secondary, null, "root@corp.example"), "re-saving one's own email is fine");
        Assert.IsNull(await provisioner.FindConflictingAccountAsync(secondary, null, "other@corp.example"), "a fresh address is fine");
    }
}
