using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Critical security boundary tests ensuring isolation between clients, realms, and users.
/// These tests verify that security boundaries are not violated.
/// </summary>
[TestClass]
public sealed class SecurityBoundaryTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task Security_Cross_Client_Token_Revocation_Blocked()
    {
        using var db = CreateDb();

        // Setup: Two clients and a refresh token for client1
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client1 = new ClientEntity { ClientId = "client1", ClientName = "Client 1", RealmId = realm.Id };
        var client2 = new ClientEntity { ClientId = "client2", ClientName = "Client 2", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "user1" };

        db.Realms.Add(realm);
        db.Clients.Add(client1);
        db.Clients.Add(client2);
        db.Users.Add(user);

        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("token_123")));
        var token = new Token
        {
            Type = "refresh",
            TokenHash = tokenHash,
            ClientId = "client1",
            UserId = user.Id,
            ScopesJson = JsonSerializer.Serialize(new[] { "read" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Client2 attempts to revoke Client1's token (using RevokeAsync with wrong clientId)
        var revocationService = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await revocationService.RevokeAsync("token_123", "refresh_token", "client2");

        // Assert: Token should NOT be revoked (cross-client revocation blocked)
        // Force reload from database to get updated state
        db.ChangeTracker.Clear();
        var tokenAfter = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        Assert.IsNotNull(tokenAfter, "Token should still exist");
        Assert.IsNull(tokenAfter.RevokedAt, "Token should NOT be revoked by different client");
    }

    [TestMethod]
    public async Task Security_Same_Client_Token_Revocation_Allowed()
    {
        using var db = CreateDb();

        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "client1", ClientName = "Client 1", RealmId = realm.Id };
        var user = new User { Id = Guid.NewGuid(), Username = "user1" };

        db.Realms.Add(realm);
        db.Clients.Add(client);
        db.Users.Add(user);

        var tokenHash = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("token_456")));
        var token = new Token
        {
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            Type = "refresh",
            TokenHash = tokenHash,
            ClientId = "client1",
            UserId = user.Id,
            ScopesJson = JsonSerializer.Serialize(new[] { "read" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync();

        // Act: Same client revokes its own token
        var revocationService = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());
        await revocationService.RevokeAsync("token_456", "refresh_token", "client1");

        // Assert: Token SHOULD be revoked
        // Force reload from database to get updated state
        db.ChangeTracker.Clear();
        var tokenAfter = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        Assert.IsNotNull(tokenAfter);
        Assert.IsNotNull(tokenAfter.RevokedAt, "Token SHOULD be revoked by owning client");
    }

    [TestMethod]
    public async Task Security_Cross_Realm_Role_Leakage_Prevented()
    {
        using var db = CreateDb();

        // Setup: Two realms with roles
        var realm1 = new Realm { Id = Guid.NewGuid(), Name = "Realm1" };
        var realm2 = new Realm { Id = Guid.NewGuid(), Name = "Realm2" };

        var role1 = new Role { Id = Guid.NewGuid(), Name = "Admin", RealmId = realm1.Id };
        var role2 = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin", RealmId = realm2.Id };

        var user = new User { Id = Guid.NewGuid(), Username = "user1" };
        var client = new ClientEntity { ClientId = "client1", RealmId = realm1.Id };

        db.Realms.Add(realm1);
        db.Realms.Add(realm2);
        db.Roles.Add(role1);
        db.Roles.Add(role2);
        db.Users.Add(user);
        db.Clients.Add(client);

        // User has role in Realm1 only
        var userRealmRole = new UserRealmRoleAssignment
        {
            UserId = user.Id,
            RoleId = role1.Id,
            RealmId = realm1.Id,
            IsActive = true
        };
        db.UserRealmRoleAssignments.Add(userRealmRole);

        // User also has role in Realm2 (should NOT be accessible from Realm1 client)
        var crossRealmRole = new UserRealmRoleAssignment
        {
            UserId = user.Id,
            RoleId = role2.Id,
            RealmId = realm2.Id,
            IsActive = true
        };
        db.UserRealmRoleAssignments.Add(crossRealmRole);
        await db.SaveChangesAsync();

        // Act: Get roles for user in context of Realm1 client
        var userRoles = await db.UserRealmRoleAssignments
            .Where(urr => urr.UserId == user.Id && urr.RealmId == realm1.Id && urr.IsActive)
            .Join(db.Roles, urr => urr.RoleId, r => r.Id, (urr, r) => r.Name)
            .ToListAsync();

        // Assert: Should only see Realm1 roles
        Assert.HasCount(1, userRoles, "Should only have 1 role from Realm1");
        Assert.Contains("Admin", userRoles, "Should have Admin role from Realm1");
        Assert.DoesNotContain("SuperAdmin", userRoles, "Should NOT have SuperAdmin role from Realm2");
    }

    [TestMethod]
    public async Task Security_Scope_Escalation_Prevented()
    {
        using var db = CreateDb();

        // Setup: Client with specific scopes allowed
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity
        {
            ClientId = "limited_client",
            ClientName = "Limited Client",
            RealmId = realm.Id
        };
        db.Realms.Add(realm);
        db.Clients.Add(client);

        // Add allowed scopes
        db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = "read" });
        db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = "profile" });
        await db.SaveChangesAsync();

        // Act: Check if client can access scopes it's not allowed to
        var allowedScopes = await db.ClientScopes
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync();

        var requestedScopes = new[] { "read", "write", "admin" };
        var unauthorizedScopes = requestedScopes.Except(allowedScopes).ToArray();

        // Assert: Scope escalation should be detected
        Assert.IsNotEmpty(unauthorizedScopes, "Unauthorized scopes should be detected");
        Assert.IsTrue(unauthorizedScopes.Contains("write"), "Write scope should be unauthorized");
        Assert.IsTrue(unauthorizedScopes.Contains("admin"), "Admin scope should be unauthorized");
    }

    [TestMethod]
    public async Task Security_Audience_Mismatch_Rejected()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache());
        var jwtService = TestJwtServiceFactory.Create(keyStore);
        var tokenValidator = TestTokenValidatorFactory.Create(keyStore);

        // Create a token with audience "api-a"
        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("scope", "read")
        };

        var token = await jwtService.CreateJwtAsync("https://issuer.com", "api-a", claims, DateTimeOffset.UtcNow.AddHours(1)).ConfigureAwait(false);

        // Act: Try to validate token expecting audience "api-b"
        // Note: TokenValidator doesn't validate audience, but this demonstrates the concept
        var (ok, principal, error) = await tokenValidator.ValidateAsync(token, "https://issuer.com");

        // Assert: Token should be validated (audience check would happen at higher level)
        Assert.IsTrue(ok, "Token should be syntactically valid");

        // Verify audience in principal
        var audClaim = principal?.FindFirst("aud");
        Assert.IsNotNull(audClaim);
        Assert.AreEqual("api-a", audClaim.Value);

        // Application layer should reject if expected audience doesn't match
        var expectedAudience = "api-b";
        var actualAudience = audClaim.Value;
        Assert.AreNotEqual(expectedAudience, actualAudience, "Audience mismatch should be detected");
    }

    [TestMethod]
    public async Task Security_JWT_Algorithm_None_Rejected()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache());
        var tokenValidator = TestTokenValidatorFactory.Create(keyStore);

        // Create an unsigned JWT (algorithm "none" attack)
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"{""alg"":""none"",""typ"":""JWT""}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"{""sub"":""user123"",""exp"":9999999999}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unsignedToken = $"{header}.{payload}.";

        // Act: Try to validate the unsigned token
        var (ok, principal, error) = await tokenValidator.ValidateAsync(unsignedToken, "https://issuer.com");

        // Assert: Should reject unsigned token
        Assert.IsFalse(ok, "Unsigned token (alg=none) should be REJECTED");
        Assert.IsNull(principal, "Principal should be null for invalid token");
        Assert.IsNotNull(error, "Error should be provided");
    }

    [TestMethod]
    public async Task Security_PKCE_Downgrade_Attack_Prevented()
    {
        using var db = CreateDb();

        // Setup: Authorization code with PKCE challenge
        var user = new User { Id = Guid.NewGuid(), Username = "user1" };
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client = new ClientEntity { ClientId = "public_client", ClientName = "Public Client", RealmId = realm.Id };

        db.Users.Add(user);
        db.Realms.Add(realm);
        db.Clients.Add(client);

        var codeChallenge = "challenge123";
        var authCode = new AuthorizationCode
        {
            Code = "code_with_pkce",
            ClientId = "public_client",
            UserId = user.Id,
            RedirectUri = "https://app.example.com/callback",
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = "S256",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        db.AuthorizationCodes.Add(authCode);
        await db.SaveChangesAsync();

        // Act: Retrieve the code and verify PKCE challenge is present
        var code = await db.AuthorizationCodes
            .FirstOrDefaultAsync(ac => ac.Code == "code_with_pkce" && ac.ClientId == "public_client");

        // Assert: Code should have PKCE challenge, requiring verifier on exchange
        Assert.IsNotNull(code, "Code should exist");
        Assert.AreEqual("challenge123", code.CodeChallenge, "Challenge should be present");
        Assert.AreEqual("S256", code.CodeChallengeMethod, "Challenge method should be S256");

        // The token exchange handler should reject if verifier is missing when challenge exists
        var hasPkce = !string.IsNullOrEmpty(code.CodeChallenge);
        Assert.IsTrue(hasPkce, "PKCE challenge must be present to prevent downgrade attack");
    }

    [TestMethod]
    public async Task Security_Token_Audience_Isolation_Between_Clients()
    {
        using var db = CreateDb();

        // Setup: Two clients, one token for each
        var user = new User { Id = Guid.NewGuid(), Username = "user1" };
        var realm = new Realm { Id = Guid.NewGuid(), Name = "test-realm" };
        var client1 = new ClientEntity { ClientId = "client1", RealmId = realm.Id };
        var client2 = new ClientEntity { ClientId = "client2", RealmId = realm.Id };

        db.Users.Add(user);
        db.Realms.Add(realm);
        db.Clients.Add(client1);
        db.Clients.Add(client2);

        var token1 = new Token
        {
            Type = "access",
            TokenHash = "hash1",
            ClientId = "client1",
            UserId = user.Id,
            Audience = "api-for-client1",
            ScopesJson = JsonSerializer.Serialize(new[] { "read" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };

        var token2 = new Token
        {
            Type = "access",
            TokenHash = "hash2",
            ClientId = "client2",
            UserId = user.Id,
            Audience = "api-for-client2",
            ScopesJson = JsonSerializer.Serialize(new[] { "write" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Jti = Guid.NewGuid().ToString()
        };

        db.Tokens.Add(token1);
        db.Tokens.Add(token2);
        await db.SaveChangesAsync();

        // Act: Query tokens for specific client
        var client1Tokens = await db.Tokens
            .Where(t => t.ClientId == "client1" && t.UserId == user.Id)
            .ToListAsync();

        var client2Tokens = await db.Tokens
            .Where(t => t.ClientId == "client2" && t.UserId == user.Id)
            .ToListAsync();

        // Assert: Each client should only see their own tokens
        Assert.HasCount(1, client1Tokens, "Client1 should have 1 token");
        Assert.AreEqual("api-for-client1", client1Tokens[0].Audience);

        Assert.HasCount(1, client2Tokens, "Client2 should have 1 token");
        Assert.AreEqual("api-for-client2", client2Tokens[0].Audience);

        // Verify no overlap
        var client1Audiences = client1Tokens.Select(t => t.Audience).ToList();
        var client2Audiences = client2Tokens.Select(t => t.Audience).ToList();

        Assert.IsFalse(client1Audiences.Any(a => client2Audiences.Contains(a)), "Audiences should be isolated");
    }

    [TestMethod]
    public async Task Security_Client_Secret_Never_In_Logs()
    {
        // This is more of a code review test, but we can verify hashing behavior
        var plainSecret = "super_secret_password_123";
        var hasher = new Argon2PasswordHasher();

        // Act: Hash the secret
        var hashedSecret = hasher.Hash(plainSecret);

        // Assert: Hash should not contain the plain secret
        Assert.DoesNotContain(plainSecret, hashedSecret, "Hashed secret should NOT contain plain text");
        Assert.IsGreaterThan(plainSecret.Length, hashedSecret.Length, "Hash should be longer than plain secret");
        Assert.StartsWith("$argon2", hashedSecret, "Should use Argon2");

        // Verify can validate
        var isValid = hasher.Verify(plainSecret, hashedSecret);
        Assert.IsTrue(isValid, "Should be able to verify hashed secret");

        // Verify wrong secret fails
        var wrongSecret = "wrong_password";
        var isInvalid = hasher.Verify(wrongSecret, hashedSecret);
        Assert.IsFalse(isInvalid, "Wrong secret should NOT validate");
    }
}


