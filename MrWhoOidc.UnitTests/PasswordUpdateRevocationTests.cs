using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// C14 of the 2026-10-04 assessment: a password reset left an attacker's sessions and tokens alive.
/// </summary>
[TestClass]
public sealed class PasswordUpdateRevocationTests
{
    [TestMethod]
    public async Task UpdatePassword_RotatesStamp_AndRevokesTokensAcrossTenants()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var account = new UserAccount { Username = "alice", Email = "alice@example.com", PasswordHash = "old", SecurityStamp = "stamp-0" };
        var aliceA = new User { TenantId = tenantA, Username = "alice", Email = "alice@example.com" };
        var aliceB = new User { TenantId = tenantB, Username = "alice-b", Email = "alice@example.com" };
        var bob = new User { TenantId = tenantA, Username = "bob", Email = "bob@example.com" };
        db.UserAccounts.Add(account);
        db.Users.AddRange(aliceA, aliceB, bob);
        db.UserTenantMemberships.AddRange(
            new UserTenantMembership { UserAccountId = account.Id, TenantId = tenantA },
            new UserTenantMembership { UserAccountId = account.Id, TenantId = tenantB });
        var future = DateTimeOffset.UtcNow.AddDays(1);
        db.Tokens.AddRange(
            new Token { TenantId = tenantA, TokenHash = "a", UserId = aliceA.Id, ClientId = "rp", ExpiresAt = future },
            new Token { TenantId = tenantB, TokenHash = "b", UserId = aliceB.Id, ClientId = "rp", ExpiresAt = future },
            new Token { TenantId = tenantA, TokenHash = "c", UserId = bob.Id, ClientId = "rp", ExpiresAt = future });
        await db.SaveChangesAsync();

        var svc = new UserAccountService(db);
        await svc.UpdatePasswordAsync(account.Id, "new-hash", null, "argon2id");

        var tokens = db.Tokens.ToDictionary(t => t.TokenHash);
        Assert.IsNotNull(tokens["a"].RevokedAt, "tenant A token must be revoked");
        Assert.IsNotNull(tokens["b"].RevokedAt, "tenant B token must be revoked");
        Assert.IsNull(tokens["c"].RevokedAt, "other users' tokens must be untouched");
        Assert.AreNotEqual("stamp-0", db.UserAccounts.Single().SecurityStamp, "security stamp must rotate to kill auth cookies");
    }

    /// <summary>
    /// F1: the password change revokes the server-side OP sessions of the account next to the stamp rotation, so
    /// the cookie of a session is rejected even though it no longer carries the old stamp.
    /// </summary>
    [TestMethod]
    public async Task UpdatePassword_RevokesTheOpSessionsOfTheAccount()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var account = new UserAccount { Username = "alice", Email = "alice@example.com", PasswordHash = "old", SecurityStamp = "stamp-0" };
        var alice = new User { TenantId = tenantId, Username = "alice", UserAccountId = account.Id };
        db.UserAccounts.Add(account);
        db.Users.Add(alice);
        await db.SaveChangesAsync();

        var sessions = new OpSessionService(
            db,
            MockTenantAccessor.CreateWithTenant(tenantId, "acme"),
            new MockTenantSettingsService(),
            Microsoft.Extensions.Options.Options.Create(new OpSessionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpSessionService>.Instance);
        var session = await sessions.CreateAsync(tenantId, alice.Id, userAccountId: account.Id);

        var svc = new UserAccountService(db, opSessions: sessions);
        await svc.UpdatePasswordAsync(account.Id, "new-hash", null, "argon2id");

        Assert.AreEqual(OpSessionStatus.Revoked, await sessions.CheckAsync(session.Id),
            "the password change ends the sessions of the account");
    }

    /// <summary>
    /// F1: the credential change keeps the stamp check for the cookies that carry no session (the compatibility
    /// path), and the session check for the cookies that do.
    /// </summary>
    [TestMethod]
    public async Task ACookieWithoutASession_IsStillRejectedByTheStamp()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var account = new UserAccount { Username = "alice", Email = "alice@example.com", PasswordHash = "old", SecurityStamp = "stamp-0" };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var svc = new UserAccountService(db);
        await svc.UpdatePasswordAsync(account.Id, "new-hash", null, "argon2id");

        Assert.AreNotEqual("stamp-0", db.UserAccounts.Single().SecurityStamp);
    }
}
