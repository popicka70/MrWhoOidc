using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RevocationServiceTests
{
    private static AuthDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [TestMethod]
    public async Task Revoke_IsIdempotent_And_Audited()
    {
        using var db = CreateDb();
        // Seed a refresh token entry
        db.Tokens.Add(new Token
        {
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            Type = "refresh",
            TokenHash = Hash("rt"),
            ClientId = "c1",
            UserId = Guid.NewGuid(),
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();

        var svc = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await svc.RevokeAsync("rt", "refresh_token", "c1", "127.0.0.1");
        await svc.RevokeAsync("rt", "refresh_token", "c1", "127.0.0.1");

        // Exactly one token is revoked
        Assert.AreEqual(1, db.Tokens.Count(t => t.Type == "refresh" && t.RevokedAt != null));
        // Two audit rows added (two calls)
        Assert.AreEqual(2, db.RevocationAudits.Count());
    }

    [TestMethod]
    public async Task Revoking_A_RefreshToken_Revokes_Its_Rotation_Family()
    {
        using var db = CreateDb();
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var userId = Guid.NewGuid();
        Token Refresh(string raw, Guid? parentId) => new()
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = Hash(raw),
            ClientId = "c1",
            UserId = userId,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            ReplacedById = parentId // lineage: points at the previous (parent) token
        };

        var first = Refresh("rt-1", null);
        var second = Refresh("rt-2", first.Id);
        var third = Refresh("rt-3", second.Id);
        var otherGrant = Refresh("rt-other", null);
        db.Tokens.AddRange(first, second, third, otherGrant);
        await db.SaveChangesAsync();

        var svc = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await svc.RevokeAsync("rt-2", "refresh_token", "c1");

        Assert.IsNotNull(db.Tokens.Single(t => t.Id == first.Id).RevokedAt, "ancestor in the family must be revoked");
        Assert.IsNotNull(db.Tokens.Single(t => t.Id == second.Id).RevokedAt);
        Assert.IsNotNull(db.Tokens.Single(t => t.Id == third.Id).RevokedAt, "the live rotated descendant must be revoked");
        Assert.IsNull(db.Tokens.Single(t => t.Id == otherGrant.Id).RevokedAt, "an unrelated grant must stay valid");
    }

    private static string Hash(string value)
    {
        return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(value);
    }
}
