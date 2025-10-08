using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RefreshTokenServiceTests
{
    private static (AuthDbContext db, IRefreshTokenService service) CreateService()
    {
        var dbName = "rt-service-" + Guid.NewGuid().ToString("N");
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var db = new AuthDbContext(opts);
        var service = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant(), new MockTenantSettingsService());
        return (db, service);
    }

    [TestMethod]
    public async Task CreateRefreshToken_Returns_Opaque_Token()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid", "profile" };

        // Act
        var (token, hash) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);

        // Assert
        Assert.IsNotNull(token);
        Assert.IsNotNull(hash);
        Assert.IsTrue(token.Length > 32, "Token should be substantial length");
        Assert.IsTrue(hash.Length > 32, "Hash should be substantial length");
        Assert.AreNotEqual(token, hash, "Token and hash should be different");
        
        // Token should be URL-safe base64
        Assert.IsFalse(token.Contains('+'));
        Assert.IsFalse(token.Contains('/'));
        Assert.IsFalse(token.Contains('='));
    }

    [TestMethod]
    public async Task CreateRefreshToken_Persists_To_Database()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid", "profile", "email" };
        var lifetime = TimeSpan.FromDays(30);

        // Act
        var (token, hash) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);

        // Assert
        var saved = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        Assert.IsNotNull(saved, "Token should be persisted");
        Assert.AreEqual("refresh", saved.Type);
        Assert.AreEqual(hash, saved.TokenHash);
        Assert.AreEqual(userId, saved.UserId);
        Assert.AreEqual(clientId, saved.ClientId);
        Assert.IsNotNull(saved.ScopesJson);
        
        var savedScopes = JsonSerializer.Deserialize<string[]>(saved.ScopesJson);
        CollectionAssert.AreEqual(scopes, savedScopes);
    }

    [TestMethod]
    public async Task CreateRefreshToken_Sets_Expiration_Correctly()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid" };
        // Note: Service now uses tenant settings (default: 1296000 seconds = 15 days)
        var expectedLifetime = TimeSpan.FromSeconds(1296000);
        var before = DateTimeOffset.UtcNow;

        // Act
        var (token, hash) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);
        var after = DateTimeOffset.UtcNow;

        // Assert
        var saved = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        Assert.IsNotNull(saved);
        
        // CreatedAt should be around now
        Assert.IsTrue(saved.CreatedAt >= before);
        Assert.IsTrue(saved.CreatedAt <= after);
        
        // ExpiresAt should be CreatedAt + expectedLifetime (within 1 second tolerance)
        var expectedExpiry = saved.CreatedAt.Add(expectedLifetime);
        var actualDifference = (saved.ExpiresAt - expectedExpiry).TotalSeconds;
        Assert.IsTrue(
            Math.Abs(actualDifference) < 1, 
            $"Expiry mismatch: expected {expectedExpiry}, got {saved.ExpiresAt}, diff={actualDifference}s");
    }

    [TestMethod]
    public async Task CreateRefreshToken_Generates_Unique_Tokens()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid" };
        var lifetime = TimeSpan.FromDays(7);

        // Act - create multiple tokens
        var (token1, hash1) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);
        var (token2, hash2) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);
        var (token3, hash3) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);

        // Assert - all should be unique
        Assert.AreNotEqual(token1, token2);
        Assert.AreNotEqual(token1, token3);
        Assert.AreNotEqual(token2, token3);
        Assert.AreNotEqual(hash1, hash2);
        Assert.AreNotEqual(hash1, hash3);
        Assert.AreNotEqual(hash2, hash3);
    }

    [TestMethod]
    public async Task CreateRefreshToken_Handles_Empty_Scopes()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = Array.Empty<string>();
        var lifetime = TimeSpan.FromDays(30);

        // Act
        var (token, hash) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);

        // Assert
        Assert.IsNotNull(token);
        Assert.IsNotNull(hash);
        
        var saved = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        Assert.IsNotNull(saved);
        var savedScopes = JsonSerializer.Deserialize<string[]>(saved.ScopesJson);
        Assert.IsNotNull(savedScopes);
        Assert.AreEqual(0, savedScopes.Length);
    }

    [TestMethod]
    public async Task CreateRefreshToken_Preserves_Multiple_Scopes()
    {
        // Arrange
        var (db, service) = CreateService();
        var userId = Guid.NewGuid();
        var clientId = "test-client";
        var scopes = new[] { "openid", "profile", "email", "address", "phone", "roles" };
        var lifetime = TimeSpan.FromDays(30);

        // Act
        var (token, hash) = await service.CreateRefreshTokenAsync(userId, clientId, scopes);

        // Assert
        var saved = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        Assert.IsNotNull(saved);
        var savedScopes = JsonSerializer.Deserialize<string[]>(saved.ScopesJson);
        Assert.IsNotNull(savedScopes);
        Assert.AreEqual(6, savedScopes.Length);
        CollectionAssert.AreEqual(scopes, savedScopes);
    }
}
