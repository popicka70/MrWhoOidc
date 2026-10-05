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

    // Reuse-detection race: rotation claims the parent first and only afterwards inserts (and used to link) the child.
    // A family revocation that ran in between walked ReplacedById and missed the child, which then stayed valid.
    // With FamilyId the child belongs to the family from the moment it exists, linked or not.
    [TestMethod]
    public async Task Family_Revocation_Covers_A_Child_Created_After_The_Parent_Was_Claimed()
    {
        using var db = CreateDb();
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var userId = Guid.NewGuid();
        var parent = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = Hash("rt-parent"),
            ClientId = "c1",
            UserId = userId,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            RevokedAt = DateTimeOffset.UtcNow // claimed by the winning rotation
        };
        parent.FamilyId = parent.Id;
        var child = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = Hash("rt-child"),
            ClientId = "c1",
            UserId = userId,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            FamilyId = parent.Id, // inherited at insert; ReplacedById not written yet
        };
        var otherFamily = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = Hash("rt-other"),
            ClientId = "c1",
            UserId = userId,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        };
        otherFamily.FamilyId = otherFamily.Id;
        db.Tokens.AddRange(parent, child, otherFamily);
        await db.SaveChangesAsync();

        var svc = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await svc.RevokeRefreshTokenFamilyAsync(parent.Id);

        Assert.IsNotNull(db.Tokens.Single(t => t.Id == child.Id).RevokedAt, "a child of the family must be revoked even before it is linked");
        Assert.IsNull(db.Tokens.Single(t => t.Id == otherFamily.Id).RevokedAt, "another grant of the same user and client must stay valid");
    }

    // Same, on a relational provider: the revocation is a single UPDATE ... WHERE FamilyId = @family.
    [TestMethod]
    public async Task Family_Revocation_Uses_FamilyId_On_A_Relational_Store()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options;
        using var db = new AuthDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "t1", Slug = "t1" });
        await db.SaveChangesAsync();

        var userId = Guid.NewGuid();
        var parent = new Token { TenantId = tenantId, Type = "refresh", TokenHash = Hash("p"), ClientId = "c1", UserId = userId, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), RevokedAt = DateTimeOffset.UtcNow };
        parent.FamilyId = parent.Id;
        var child = new Token { TenantId = tenantId, Type = "refresh", TokenHash = Hash("c"), ClientId = "c1", UserId = userId, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), FamilyId = parent.Id };
        var other = new Token { TenantId = tenantId, Type = "refresh", TokenHash = Hash("o"), ClientId = "c1", UserId = userId, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        other.FamilyId = other.Id;
        db.Tokens.AddRange(parent, child, other);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var svc = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await svc.RevokeRefreshTokenFamilyAsync(parent.Id);

        Assert.IsNotNull(await db.Tokens.AsNoTracking().Where(t => t.Id == child.Id).Select(t => t.RevokedAt).SingleAsync());
        Assert.IsNull(await db.Tokens.AsNoTracking().Where(t => t.Id == other.Id).Select(t => t.RevokedAt).SingleAsync());
    }

    private static string Hash(string value)
    {
        return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(value);
    }
}
