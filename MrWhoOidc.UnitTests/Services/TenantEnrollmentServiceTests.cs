using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public sealed class TenantEnrollmentServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task CreateInvitationAsync_CreatesPendingInvitationWithHashedToken()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var service = CreateService(db);

        var result = await service.CreateInvitationAsync(
            tenant.Id,
            "New.User@Example.com",
            "New User",
            isTenantAdmin: false,
            TimeSpan.FromDays(7),
            invitedByUserId: null,
            invitedByUsername: "admin@example.com");

        Assert.IsTrue(result.Token.StartsWith("inv_", StringComparison.Ordinal));
        Assert.AreNotEqual(result.Token, result.Invitation.TokenHash);
        Assert.AreEqual("new.user@example.com", result.Invitation.NormalizedEmail);
        Assert.AreEqual(TenantInvitationStatus.Pending, result.Invitation.Status);
    }

    [TestMethod]
    public async Task AcceptInvitationAsync_ForMatchingAccount_CreatesTenantUserAndMembership()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var account = await SeedAccountAsync(db, "member@example.com");
        var service = CreateService(db);
        var invite = await service.CreateInvitationAsync(
            tenant.Id,
            "member@example.com",
            "Member User",
            isTenantAdmin: false,
            TimeSpan.FromDays(7),
            invitedByUserId: null,
            invitedByUsername: null);

        var result = await service.AcceptInvitationAsync(invite.Token, account.Id);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(tenant.Id, result.TenantId);

        var membership = await db.UserTenantMemberships.SingleAsync(m => m.UserAccountId == account.Id && m.TenantId == tenant.Id);
        Assert.AreEqual(TenantMembershipStatus.Active, membership.Status);
        Assert.IsFalse(membership.IsTenantAdmin);

        var user = await db.Users.SingleAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == "member@example.com");
        Assert.AreEqual("Member User", user.Name);

        var storedInvite = await db.TenantInvitations.SingleAsync(i => i.Id == invite.Invitation.Id);
        Assert.AreEqual(TenantInvitationStatus.Accepted, storedInvite.Status);
        Assert.AreEqual(account.Id, storedInvite.AcceptedByUserAccountId);
    }

    [TestMethod]
    public async Task AcceptInvitationAsync_ForTenantAdminInvite_AssignsTenantAdminRole()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var account = await SeedAccountAsync(db, "admin-invite@example.com");
        var service = CreateService(db);
        var invite = await service.CreateInvitationAsync(
            tenant.Id,
            "admin-invite@example.com",
            "Admin Invite",
            isTenantAdmin: true,
            TimeSpan.FromDays(7),
            invitedByUserId: null,
            invitedByUsername: null);

        var result = await service.AcceptInvitationAsync(invite.Token, account.Id);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        var user = await db.Users.SingleAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == "admin-invite@example.com");
        var role = await db.Roles.SingleAsync(r => r.TenantId == tenant.Id && r.Name == "tenant-admin");
        Assert.IsTrue(await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == user.Id && a.RoleId == role.Id && a.IsActive));
    }

    [TestMethod]
    public async Task AcceptInvitationForUserAsync_ForRegisteredUser_MarksInvitationAccepted()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var account = await SeedAccountAsync(db, "registered@example.com");
        var registeredUser = new User
        {
            TenantId = tenant.Id,
            Username = "registered@example.com",
            Email = "registered@example.com",
            NormalizedEmail = "registered@example.com",
            Name = "Registered User"
        };
        db.Users.Add(registeredUser);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var invite = await service.CreateInvitationAsync(
            tenant.Id,
            "registered@example.com",
            "Registered User",
            isTenantAdmin: false,
            TimeSpan.FromDays(7),
            invitedByUserId: null,
            invitedByUsername: null);

        var result = await service.AcceptInvitationForUserAsync(invite.Token, registeredUser.Id);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(registeredUser.Id, result.UserId);
        Assert.AreEqual(account.Id, result.UserAccountId);
        Assert.AreEqual(TenantInvitationStatus.Accepted, await db.TenantInvitations
            .Where(i => i.Id == invite.Invitation.Id)
            .Select(i => i.Status)
            .SingleAsync());
    }

    [TestMethod]
    public async Task AcceptInvitationAsync_ForMismatchedEmail_ReturnsFailureWithoutMembership()
    {
        using var db = CreateDb();
        var tenant = await SeedTenantAsync(db);
        var account = await SeedAccountAsync(db, "different@example.com");
        var service = CreateService(db);
        var invite = await service.CreateInvitationAsync(
            tenant.Id,
            "invited@example.com",
            null,
            isTenantAdmin: false,
            TimeSpan.FromDays(7),
            invitedByUserId: null,
            invitedByUsername: null);

        var result = await service.AcceptInvitationAsync(invite.Token, account.Id);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("email_mismatch", result.ErrorCode);
        Assert.IsFalse(await db.UserTenantMemberships.AnyAsync(m => m.UserAccountId == account.Id && m.TenantId == tenant.Id));
    }

    private static ITenantEnrollmentService CreateService(AuthDbContext db)
        => new TenantEnrollmentService(db, NullLogger<TenantEnrollmentService>.Instance);

    private static async Task<Tenant> SeedTenantAsync(AuthDbContext db)
    {
        var tenant = new Tenant
        {
            Slug = "acme",
            Name = "Acme",
            IssuerUri = "https://localhost:8443/t/acme",
            Status = TenantStatus.Active
        };
        db.Tenants.Add(tenant);

        var realm = new Realm
        {
            TenantId = tenant.Id,
            Name = "default",
            DisplayName = "Default Realm"
        };
        db.Realms.Add(realm);

        db.Roles.Add(new Role
        {
            TenantId = tenant.Id,
            RealmId = realm.Id,
            Name = "tenant-admin",
            IsActive = true
        });

        await db.SaveChangesAsync();
        return tenant;
    }

    private static async Task<UserAccount> SeedAccountAsync(AuthDbContext db, string email)
    {
        var account = new UserAccount
        {
            Username = email,
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            EmailVerified = true,
            PasswordHash = "hash",
            HashAlgorithm = "argon2id",
            Name = "Existing User"
        };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }
}