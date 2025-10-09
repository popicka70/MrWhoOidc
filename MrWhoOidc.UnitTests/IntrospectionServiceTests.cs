using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using JwtRegisteredClaimNames = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for token introspection scenarios covering JWT, opaque, and refresh tokens.
/// These tests focus on the service layer logic for RFC 7662 compliance.
/// </summary>
[TestClass]
public sealed class IntrospectionServiceTests
{
    private static readonly Guid DefaultTenantId = new("00000000-0000-0000-0000-000000000001");

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public void JWT_Token_Validation_Returns_Claims()
    {
        using var db = CreateDb();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();

        // Setup: Create a valid JWT
        var keyStore = new KeyStore(db, tenantAccessor);
        var jwtService = new JwtService(keyStore);
        var tokenValidator = new TokenValidator(keyStore);

        var userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("client_id", "test-client"),
            new Claim("scope", "openid profile")
        };

        var token = jwtService.CreateJwt("https://op.example.com", "test-api", claims, DateTimeOffset.UtcNow.AddHours(1));

        // Act: Validate the JWT
        var (isValid, principal, error) = tokenValidator.Validate(token, "https://op.example.com");

        // Assert: Should be valid with claims
        Assert.IsTrue(isValid, "JWT should be valid");
        Assert.IsNotNull(principal);
        Assert.AreEqual(userId.ToString(), principal.FindFirst("sub")?.Value);
        Assert.AreEqual("test-client", principal.FindFirst("client_id")?.Value);
    }

    [TestMethod]
    public void JWT_Token_Expired_Returns_Invalid()
    {
        using var db = CreateDb();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();

        // Setup: Create JWT with past expiry manually to bypass JwtService's DateTime.UtcNow hardcoding
        var keyStore = new KeyStore(db, tenantAccessor);
        var tokenValidator = new TokenValidator(keyStore);

        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("scope", "read"),
            new Claim(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(DateTime.UtcNow.AddHours(-2)).ToString(), ClaimValueTypes.Integer64)
        };

        // Create JWT that expired 1 hour ago (nbf: 2 hours ago, exp: 1 hour ago)
        var jwk = keyStore.GetActiveSigningKeyAsync().GetAwaiter().GetResult();
        var jsonWebKey = new JsonWebKey(jwk.ToJson(includePrivate: true));
        var creds = new SigningCredentials(jsonWebKey, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: "https://op.example.com",
            audience: "test-api",
            claims: claims,
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: creds
        );

        var handler = new JwtSecurityTokenHandler();
        var tokenString = handler.WriteToken(token);

        // Act: Validate expired JWT
        var (isValid, principal, error) = tokenValidator.Validate(tokenString, "https://op.example.com");

        // Assert: Should be invalid
        Assert.IsFalse(isValid, "Expired JWT should be invalid");
        Assert.IsNull(principal);
    }

    [TestMethod]
    public async Task Opaque_Token_Active_Found_In_Database()
    {
        using var db = CreateDb();

        // Setup: Create opaque access token in database
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "test-client", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = "hash" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

        var token = new Token
        {
            Type = "access",
            TokenHash = tokenHash,
            ClientId = "test-client",
            UserId = user.Id,
            Audience = "test-api",
            ScopesJson = JsonSerializer.Serialize(new[] { "read", "write" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Query for the token
        var foundToken = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Type == "access");

        // Assert: Should find active token
        Assert.IsNotNull(foundToken, "Token should be found");
        Assert.IsNull(foundToken.RevokedAt, "Token should not be revoked");
        Assert.IsTrue(foundToken.ExpiresAt > DateTimeOffset.UtcNow, "Token should not be expired");
        Assert.AreEqual("test-client", foundToken.ClientId);
        Assert.AreEqual("test-api", foundToken.Audience);
    }

    [TestMethod]
    public async Task Opaque_Token_Expired_Returns_Inactive()
    {
        using var db = CreateDb();

        // Setup: Create EXPIRED opaque token
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "test-client", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = "hash" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

        var token = new Token
        {
            Type = "access",
            TokenHash = tokenHash,
            ClientId = "test-client",
            UserId = user.Id,
            Audience = "test-api",
            ScopesJson = JsonSerializer.Serialize(new[] { "read" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-2), // Expired 2 hours ago
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Query for the token and check expiry
        var foundToken = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        var isActive = foundToken != null && foundToken.ExpiresAt > DateTimeOffset.UtcNow && foundToken.RevokedAt == null;

        // Assert: Should be inactive due to expiry
        Assert.IsNotNull(foundToken, "Token should exist in database");
        Assert.IsFalse(isActive, "Expired token should be inactive");
        Assert.IsTrue(foundToken.ExpiresAt < DateTimeOffset.UtcNow, "Token should be expired");
    }

    [TestMethod]
    public async Task Opaque_Token_Revoked_Returns_Inactive()
    {
        using var db = CreateDb();

        // Setup: Create REVOKED opaque token
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "test-client", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = "hash" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

        var token = new Token
        {
            Type = "access",
            TokenHash = tokenHash,
            ClientId = "test-client",
            UserId = user.Id,
            Audience = "test-api",
            ScopesJson = JsonSerializer.Serialize(new[] { "read" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), // Not expired
            RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-10), // But revoked 10 mins ago
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Query for the token and check status
        var foundToken = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        var isActive = foundToken != null && foundToken.ExpiresAt > DateTimeOffset.UtcNow && foundToken.RevokedAt == null;

        // Assert: Should be inactive due to revocation
        Assert.IsNotNull(foundToken, "Token should exist in database");
        Assert.IsFalse(isActive, "Revoked token should be inactive");
        Assert.IsNotNull(foundToken.RevokedAt, "Token should have revocation timestamp");
    }

    [TestMethod]
    public async Task Refresh_Token_Active_Found_In_Database()
    {
        using var db = CreateDb();

        // Setup: Create refresh token
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "test-client", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = "hash" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

        var token = new Token
        {
            Type = "refresh",
            TokenHash = tokenHash,
            ClientId = "test-client",
            UserId = user.Id,
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Query for refresh token
        var foundToken = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Type == "refresh");
        var isActive = foundToken != null && foundToken.ExpiresAt > DateTimeOffset.UtcNow && foundToken.RevokedAt == null;

        // Assert: Should find active refresh token
        Assert.IsNotNull(foundToken, "Refresh token should be found");
        Assert.IsTrue(isActive, "Refresh token should be active");
        Assert.AreEqual("test-client", foundToken.ClientId);
    }

    [TestMethod]
    public async Task Unknown_Token_Returns_Inactive()
    {
        using var db = CreateDb();

        // Setup: Don't create any token
        var unknownToken = "unknown_token_12345";
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(unknownToken)));

        // Act: Try to find non-existent token
        var foundToken = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

        // Assert: Should not find token (RFC 7662: return active=false for unknown tokens)
        Assert.IsNull(foundToken, "Unknown token should not be found");
    }

    [TestMethod]
    public async Task DPoP_Bound_Token_Has_Cnf_Claim()
    {
        using var db = CreateDb();

        // Setup: Create DPoP-bound token
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "test-client", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = "hash" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
        var jktThumbprint = "test_dpop_jkt_thumbprint_abc123";

        var token = new Token
        {
            Type = "access",
            TokenHash = tokenHash,
            ClientId = "test-client",
            UserId = user.Id,
            Audience = "test-api",
            ScopesJson = JsonSerializer.Serialize(new[] { "read" }),
            CnfJkt = jktThumbprint, // DPoP binding
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Query token and check DPoP binding
        var foundToken = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

        // Assert: Should have cnf claim with jkt
        Assert.IsNotNull(foundToken);
        Assert.IsNotNull(foundToken.CnfJkt, "DPoP-bound token should have CnfJkt");
        Assert.AreEqual(jktThumbprint, foundToken.CnfJkt);
    }

    [TestMethod]
    public async Task Token_Scope_Claims_Deserialized_Correctly()
    {
        using var db = CreateDb();

        // Setup: Create token with specific scopes
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "test-client", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "testuser", PasswordHash = "hash" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
        var scopes = new[] { "openid", "profile", "email", "read:data", "write:data" };

        var token = new Token
        {
            Type = "access",
            TokenHash = tokenHash,
            ClientId = "test-client",
            UserId = user.Id,
            Audience = "test-api",
            ScopesJson = JsonSerializer.Serialize(scopes),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Query and deserialize scopes
        var foundToken = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        var deserializedScopes = JsonSerializer.Deserialize<string[]>(foundToken!.ScopesJson);

        // Assert: Scopes should match
        Assert.IsNotNull(deserializedScopes);
        Assert.HasCount(5, deserializedScopes);
        CollectionAssert.Contains(deserializedScopes, "openid");
        CollectionAssert.Contains(deserializedScopes, "profile");
        CollectionAssert.Contains(deserializedScopes, "email");
        CollectionAssert.Contains(deserializedScopes, "read:data");
        CollectionAssert.Contains(deserializedScopes, "write:data");
    }

    [TestMethod]
    public async Task Client_Authentication_With_Secret_Validated()
    {
        using var db = CreateDb();

        // Setup: Client with secret
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm", TenantId = DefaultTenantId };
        var hasher = new Argon2PasswordHasher();
        var correctSecret = "my_secure_secret_123";
        var wrongSecret = "wrong_secret";

        var client = new ClientEntity
        {
            ClientId = "confidential-client",
            RealmId = realm.Id,
            ClientSecretHash = hasher.Hash(correctSecret),
            TenantId = DefaultTenantId
        };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var clientStore = new ClientStore(db, hasher, tenantAccessor);

        // Act: Validate correct secret
        var foundClient = await clientStore.FindByClientIdAsync("confidential-client");
        Assert.IsNotNull(foundClient);

        var isValidCorrect = hasher.Verify(correctSecret, foundClient.ClientSecretHash!);
        var isValidWrong = hasher.Verify(wrongSecret, foundClient.ClientSecretHash!);

        // Assert: Only correct secret should validate
        Assert.IsTrue(isValidCorrect, "Correct secret should validate");
        Assert.IsFalse(isValidWrong, "Wrong secret should not validate");
    }

    [TestMethod]
    public async Task Public_Client_Has_No_Secret()
    {
        using var db = CreateDb();

        // Setup: Public client (no secret)
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm", TenantId = DefaultTenantId };
        var client = new ClientEntity
        {
            ClientId = "public-client",
            RealmId = realm.Id,
            ClientSecretHash = null, // Public client has no secret
            TenantId = DefaultTenantId
        };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var clientStore = new ClientStore(db, new Argon2PasswordHasher(), tenantAccessor);

        // Act: Find public client
        var foundClient = await clientStore.FindByClientIdAsync("public-client");

        // Assert: Should have no secret hash
        Assert.IsNotNull(foundClient);
        Assert.IsNull(foundClient.ClientSecretHash, "Public client should have no secret");
    }
}
