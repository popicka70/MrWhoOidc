using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for refresh token revocation logic via RevocationService.
/// Token creation tested in RefreshTokenServiceTests.
/// Token exchange/validation tested in existing TokenServiceTests and grant handler tests.
/// </summary>
[TestClass]
public sealed class RefreshTokenRevocationTests
{
    private static (AuthDbContext db, RevocationService service) CreateService()
    {
        var dbName = "rt-revoke-" + Guid.NewGuid().ToString("N");
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var db = new AuthDbContext(opts);
        var service = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        return (db, service);
    }

    private static async Task<string> CreateRefreshTokenInDb(AuthDbContext db, Guid userId, string clientId, string[] scopes, DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null)
    {
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        db.Tokens.Add(new Token
        {
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            Type = "refresh",
            TokenHash = hash,
            UserId = userId,
            ClientId = clientId,
            ScopesJson = JsonSerializer.Serialize(scopes),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(30),
            RevokedAt = revokedAt
        });
        await db.SaveChangesAsync();
        return token;
    }

    [TestMethod]
    public async Task RevokeAsync_Marks_Token_As_Revoked()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid" };

        var token = await CreateRefreshTokenInDb(db, userId, clientId, scopes);
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        // Act
        await service.RevokeAsync(token, "refresh_token", clientId);

        // Assert
        var saved = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        Assert.IsNotNull(saved);
        Assert.IsNotNull(saved.RevokedAt, "Token should be marked as revoked");
        Assert.IsTrue(saved.RevokedAt <= DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task RevokeAsync_Creates_Audit_Record()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid" };

        var token = await CreateRefreshTokenInDb(db, userId, clientId, scopes);
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        // Act
        await service.RevokeAsync(token, "refresh_token", clientId, ipAddress: "192.168.1.1");

        // Assert
        var audit = await db.RevocationAudits.FirstOrDefaultAsync(a => a.TokenHash == hash);
        Assert.IsNotNull(audit, "Audit record should be created");
        Assert.AreEqual(clientId, audit.ClientId);
        Assert.AreEqual("refresh_token", audit.TokenType);
        Assert.AreEqual("192.168.1.1", audit.IpAddress);
    }

    [TestMethod]
    public async Task RevokeAsync_Already_Revoked_Is_Idempotent()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid" };

        var revokedTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = await CreateRefreshTokenInDb(db, userId, clientId, scopes, revokedAt: revokedTime);

        // Act - revoke again
        await service.RevokeAsync(token, "refresh_token", clientId);

        // Assert - should complete without error (idempotent)
        var saved = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash ==
            Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))));
        Assert.IsNotNull(saved);
        Assert.AreEqual(revokedTime, saved.RevokedAt, "Original revocation time should be preserved");
    }

    [TestMethod]
    public async Task RevokeAsync_Nonexistent_Token_Creates_Audit_Only()
    {
        // Arrange
        var (db, service) = CreateService();
        var nonExistentToken = "nonexistent-token-value";
        var clientId = "test-client";

        // Act
        await service.RevokeAsync(nonExistentToken, "refresh_token", clientId);

        // Assert - audit should be created even for nonexistent token
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nonExistentToken)));
        var audit = await db.RevocationAudits.FirstOrDefaultAsync(a => a.TokenHash == hash);
        Assert.IsNotNull(audit, "Audit record should be created for nonexistent token");
    }

    [TestMethod]
    public async Task RevokeAsync_Wrong_ClientId_Does_Not_Revoke()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var originalClientId = "client-1";
        var differentClientId = "client-2";
        var scopes = new[] { "openid" };

        var token = await CreateRefreshTokenInDb(db, userId, originalClientId, scopes);
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        // Act - Try to revoke with different client
        await service.RevokeAsync(token, "refresh_token", differentClientId);

        // Assert - token should NOT be revoked
        var saved = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        Assert.IsNotNull(saved);
        Assert.IsNull(saved.RevokedAt, "Token should NOT be revoked with wrong client ID");

        // But audit should still be created
        var audit = await db.RevocationAudits.FirstOrDefaultAsync(a => a.TokenHash == hash);
        Assert.IsNotNull(audit);
    }

    [TestMethod]
    public async Task RevokeRefreshTokenFamilyAsync_Revokes_Entire_Lineage()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "lineage-client";

        var root = new Token
        {
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            Type = "refresh",
            TokenHash = "h-root",
            UserId = userId,
            ClientId = clientId,
            ScopesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(10)
        };
        var mid = new Token
        {
            TenantId = root.TenantId,
            Type = "refresh",
            TokenHash = "h-mid",
            UserId = userId,
            ClientId = clientId,
            ScopesJson = "[]",
            CreatedAt = root.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(10),
            ReplacedById = root.Id
        };
        var tip = new Token
        {
            TenantId = root.TenantId,
            Type = "refresh",
            TokenHash = "h-tip",
            UserId = userId,
            ClientId = clientId,
            ScopesJson = "[]",
            CreatedAt = root.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(10),
            ReplacedById = mid.Id
        };

        db.Tokens.AddRange(root, mid, tip);
        await db.SaveChangesAsync();

        // Act
        await service.RevokeRefreshTokenFamilyAsync(mid.Id);

        // Assert
        var reloaded = await db.Tokens.Where(t => t.ClientId == clientId).ToListAsync();
        Assert.AreEqual(3, reloaded.Count);
        Assert.IsTrue(reloaded.All(t => t.RevokedAt != null), "All tokens in lineage should be revoked");
    }

    [TestMethod]
    public async Task RevokeRefreshTokenFamilyAsync_Does_Not_Revoke_Unrelated_Family()
    {
        // Arrange
        var (db, service) = CreateService();
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var userId = Guid.NewGuid();
        var clientId = "client-a";

        var familyA1 = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = "fa1",
            UserId = userId,
            ClientId = clientId,
            ScopesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(2)
        };
        var familyA2 = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = "fa2",
            UserId = userId,
            ClientId = clientId,
            ScopesJson = "[]",
            CreatedAt = familyA1.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(2),
            ReplacedById = familyA1.Id
        };

        var familyB1 = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = "fb1",
            UserId = userId,
            ClientId = clientId,
            ScopesJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(2)
        };

        db.Tokens.AddRange(familyA1, familyA2, familyB1);
        await db.SaveChangesAsync();

        // Act
        await service.RevokeRefreshTokenFamilyAsync(familyA2.Id);

        // Assert
        var tA1 = await db.Tokens.FindAsync(familyA1.Id);
        var tA2 = await db.Tokens.FindAsync(familyA2.Id);
        var tB1 = await db.Tokens.FindAsync(familyB1.Id);

        Assert.IsNotNull(tA1);
        Assert.IsNotNull(tA2);
        Assert.IsNotNull(tB1);
        Assert.IsNotNull(tA1.RevokedAt);
        Assert.IsNotNull(tA2.RevokedAt);
        Assert.IsNull(tB1.RevokedAt, "Unrelated refresh family must remain active");
    }
}
