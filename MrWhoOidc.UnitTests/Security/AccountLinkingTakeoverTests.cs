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

        await Assert.ThrowsExactlyAsync<AccountLinkConflictException>(
            () => CreateProvisioner(db).EnsureAsync(squatter, AttackerTenant, null, isTenantAdmin: false));

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

    [TestMethod]
    public async Task FindForUser_LinkedUser_IgnoresMatchingEmailOfAnotherAccount()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        var mallory = new UserAccount { Username = "mallory", Email = "mallory@evil.example", NormalizedEmail = "mallory@evil.example", PasswordHash = "h" };
        // Legacy poisoned row: the tenant user carries the victim's email but is linked to mallory.
        var user = new User { TenantId = AttackerTenant, Username = "mallory", Email = "root@corp.example", NormalizedEmail = "root@corp.example", UserAccountId = mallory.Id };
        db.UserAccounts.Add(mallory);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var account = await new UserAccountService(db).FindForUserAsync(user);

        Assert.AreEqual(mallory.Id, account?.Id, "the link, not the email, decides the account");
        Assert.AreNotEqual(victim.Id, account?.Id);
    }

    [TestMethod]
    public async Task EnsureAsync_LinksNewUserToItsAccount()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var user = new User { TenantId = HomeTenant, Username = "neo", Email = "neo@example.com", NormalizedEmail = "neo@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await CreateProvisioner(db).EnsureAsync(user, HomeTenant, null, isTenantAdmin: false);

        Assert.AreEqual(user.Id, db.Users.Single(u => u.Id == user.Id).UserAccountId);
    }

    [TestMethod]
    public async Task EnsureAsync_SecondUserForSameAccountInTenant_IsNotLinked()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        db.Users.Single(u => u.Id == victim.Id).UserAccountId = victim.Id;
        // A second home-tenant row matching the account by email (legacy duplicate).
        var duplicate = new User { TenantId = HomeTenant, Username = "root-dup", Email = "root@corp.example", NormalizedEmail = "root@corp.example" };
        db.Users.Add(duplicate);
        await db.SaveChangesAsync();

        await CreateProvisioner(db).EnsureAsync(duplicate, HomeTenant, null, isTenantAdmin: false, linkMode: AccountLinkMode.TrustedIdentifierMatch);

        Assert.IsNull(db.Users.Single(u => u.Id == duplicate.Id).UserAccountId, "one user per account per tenant");
    }

    // V3/V4 of the third 2026-10-04 review: a *new* unlinked user (registration approval, external
    // auto-provisioning, tenant seeding) adopted whichever account had its username or email.

    [TestMethod]
    [DataRow("someone-else", "root@corp.example")]
    [DataRow("root", "fresh@evil.example")]
    public async Task EnsureAsync_NewUserMatchingForeignAccount_IsRefusedWithoutLinkOrMembership(string username, string email)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        var newcomer = new User { TenantId = AttackerTenant, Username = username, Email = email, NormalizedEmail = email };
        db.Users.Add(newcomer);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsExactlyAsync<AccountLinkConflictException>(
            () => CreateProvisioner(db).EnsureAsync(newcomer, AttackerTenant, null, isTenantAdmin: true));

        Assert.AreEqual(victim.Id, ex.AccountId);
        Assert.IsNull(db.Users.Single(u => u.Id == newcomer.Id).UserAccountId, "the new user must not be linked to the victim's account");
        Assert.IsFalse(db.UserTenantMemberships.Any(m => m.UserAccountId == victim.Id && m.TenantId == AttackerTenant),
            "the victim must not be enrolled in the attacker's tenant");
    }

    [TestMethod]
    public async Task EnsureAsync_NewUserWithFreshIdentifiers_GetsItsOwnLinkedAccount()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        await SeedVictimAsync(db);
        var newcomer = new User { TenantId = AttackerTenant, Username = "fresh", Email = "fresh@example.com", NormalizedEmail = "fresh@example.com" };
        db.Users.Add(newcomer);
        await db.SaveChangesAsync();

        await CreateProvisioner(db).EnsureAsync(newcomer, AttackerTenant, null, isTenantAdmin: false);

        Assert.AreEqual(newcomer.Id, db.Users.Single(u => u.Id == newcomer.Id).UserAccountId);
        Assert.IsTrue(db.UserTenantMemberships.Any(m => m.UserAccountId == newcomer.Id && m.TenantId == AttackerTenant));
    }

    [TestMethod]
    public async Task EnsureAsync_TrustedOperatorSeed_StillLinksTheSamePersonByEmail()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        var secondary = new User { TenantId = AttackerTenant, Username = "root", Email = "root@corp.example", NormalizedEmail = "root@corp.example" };
        db.Users.Add(secondary);
        await db.SaveChangesAsync();

        await CreateProvisioner(db).EnsureAsync(secondary, AttackerTenant, null, isTenantAdmin: false, linkMode: AccountLinkMode.TrustedIdentifierMatch);

        Assert.AreEqual(victim.Id, db.Users.Single(u => u.Id == secondary.Id).UserAccountId);
    }

    [TestMethod]
    public async Task FindConflictingAccount_UnlinkedUserSharingVictimUsername_CannotTakeVictimEmail()
    {
        // K2 residue: the username fallback excluded the victim's account from the conflict check.
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        var legacy = new User { TenantId = AttackerTenant, Username = "root", Email = "legacy@evil.example", NormalizedEmail = "legacy@evil.example" };
        db.Users.Add(legacy);
        await db.SaveChangesAsync();

        var conflict = await CreateProvisioner(db).FindConflictingAccountAsync(legacy, null, "root@corp.example");

        Assert.AreEqual(victim.Id, conflict?.Id);
    }

    [TestMethod]
    public async Task RegistrationApproval_EmailOfAccountInAnotherTenant_IsRejectedAndPasswordNotPlanted()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var victim = await SeedVictimAsync(db);
        victim.PasswordHash = string.Empty; // e.g. created by external-IdP provisioning or admin Add
        // The per-tenant duplicate check only sees users of the registering tenant; make sure it is not what stops
        // this registration (the in-memory test db has no tenant filter).
        var home = db.Users.Single(u => u.Id == victim.Id);
        home.Email = home.NormalizedEmail = "renamed@corp.example";
        await db.SaveChangesAsync();

        var svc = new MrWhoOidc.Auth.Services.Users.RegistrationService(
            db,
            NullLogger<MrWhoOidc.Auth.Services.Users.RegistrationService>.Instance,
            Moq.Mock.Of<MrWhoOidc.Auth.MultiTenancy.IIssuerBuilder>(),
            Options.Create(new OidcOptions()),
            CreateProvisioner(db));

        var outcome = MrWhoOidc.Auth.Services.Users.RegistrationOutcome.Approved;
        try
        {
            var result = await svc.CreateRegistrationAsync(new MrWhoOidc.Auth.Services.Users.RegistrationInput(
                Email: "root@corp.example",
                FirstName: null,
                LastName: null,
                ClientId: null,
                PasswordHash: "attacker-hash",
                AutoApprove: true,
                IsExternalIdp: false,
                TenantCreation: null,
                TargetTenantId: AttackerTenant));
            outcome = result.Outcome;
        }
        catch (InvalidOperationException)
        {
            outcome = MrWhoOidc.Auth.Services.Users.RegistrationOutcome.ExistingUser;
        }

        Assert.AreNotEqual(MrWhoOidc.Auth.Services.Users.RegistrationOutcome.Approved, outcome);
        Assert.AreEqual(string.Empty, db.UserAccounts.Single(a => a.Id == victim.Id).PasswordHash, "the registrant's password must not land on the victim's account");
        Assert.IsFalse(db.UserTenantMemberships.Any(m => m.UserAccountId == victim.Id && m.TenantId == AttackerTenant));
    }
}
