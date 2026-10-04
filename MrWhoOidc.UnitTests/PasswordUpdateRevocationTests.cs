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
}
