using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenExchangePolicyTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<AuthOptions> Options(params string[] audiences)
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            ApiAudiences = audiences is { Length: > 0 } ? audiences : new[] { "api" },
            EnableTokenExchange = true
        });

    [TestMethod]
    public async Task TokenExchange_DPoP_SameKey_Required_ByPolicy()
    {
        using var db = CreateDb();
        // Seed caller client with OboDpopMode.RequireSameJkt
    var callerClient = new ClientEntity { ClientId = "caller-app", RealmId = Guid.NewGuid(), OboDpopMode = OboDpopMode.RequireSameJkt };
        db.Clients.Add(callerClient);
    await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cnfJson = System.Text.Json.JsonSerializer.Serialize(new { jkt = "abc" });
        var subject = jwt.CreateJwt(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read"), new Claim("cnf", cnfJson) },
            expires: now.AddMinutes(10)
        );

        // Without DPoP or with wrong jkt -> should fail
        var fail1 = await svc.ExchangeTokenAsync(subject, "urn:ietf:params:oauth:token-type:access_token", null, null, Array.Empty<string>(), "caller-app", "https://issuer", null);
        Assert.IsFalse(fail1.ok);
        Assert.AreEqual(400, fail1.status);

        var fail2 = await svc.ExchangeTokenAsync(subject, "urn:ietf:params:oauth:token-type:access_token", null, null, Array.Empty<string>(), "caller-app", "https://issuer", "zzz");
        Assert.IsFalse(fail2.ok);
        Assert.AreEqual(400, fail2.status);

        // With matching jkt -> succeed
        var ok = await svc.ExchangeTokenAsync(subject, "urn:ietf:params:oauth:token-type:access_token", null, null, Array.Empty<string>(), "caller-app", "https://issuer", "abc");
        Assert.IsTrue(ok.ok);
        Assert.AreEqual(200, ok.status);
    }

    [TestMethod]
    public async Task TokenExchange_OpaqueSubject_Respects_MaxDelegationDepth()
    {
        using var db = CreateDb();
        // Seed caller client with max depth = 1
    var callerClient = new ClientEntity { ClientId = "caller-app", RealmId = Guid.NewGuid(), OboMaxDelegationDepth = 1 };
        db.Clients.Add(callerClient);
    await db.SaveChangesAsync();

        // Seed an opaque subject token with DelegationDepth = 1 (already used once)
        var userId = Guid.NewGuid();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw)));
        db.Tokens.Add(new Token
        {
            Type = "access",
            TokenHash = hash,
            UserId = userId,
            ClientId = "some-client",
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "read" }),
            Audience = "api",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            DelegationDepth = 1
        });
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, new OboPolicyService(db, opts));

        var result = await svc.ExchangeTokenAsync(raw, null, null, null, Array.Empty<string>(), "caller-app", "https://issuer", null);
        Assert.IsFalse(result.ok);
        Assert.AreEqual(400, result.status);
        Assert.AreEqual("invalid_grant", result.error);
        var json = System.Text.Json.JsonSerializer.Serialize(result.payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("max_delegation_depth_exceeded", doc.RootElement.GetProperty("error_description").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_Invalid_Target_Audience_ByPolicy()
    {
        using var db = CreateDb();
        // Seed caller client with allowed target audiences = ["api-b"] only
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboAllowedTargetAudiencesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "api-b" })
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api-a", "api-b");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        var subject = jwt.CreateJwt("https://issuer", "api-a", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10));

        // Try to target api-a (not allowed by policy)
        var result = await svc.ExchangeTokenAsync(subject, null, null, "api-a", new[] { "read" }, "caller-app", "https://issuer", null);
        Assert.IsFalse(result.ok);
        Assert.AreEqual(400, result.status);
        Assert.AreEqual("invalid_target", result.error);
    }

    [TestMethod]
    public async Task TokenExchange_Invalid_Source_Audience_ByPolicy()
    {
        using var db = CreateDb();
        // Seed caller client allowing only source audience = api-x
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboAllowedSourceAudiencesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "api-x" })
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api-a", "api-b");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        // Subject from api-a (not allowed as source)
        var subject = jwt.CreateJwt("https://issuer", "api-a", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10));

        var result = await svc.ExchangeTokenAsync(subject, null, null, "api-b", new[] { "read" }, "caller-app", "https://issuer", null);
        Assert.IsFalse(result.ok);
        Assert.AreEqual(400, result.status);
        Assert.AreEqual("invalid_grant", result.error);
    }

    [TestMethod]
    public async Task TokenExchange_Insufficient_Scope_WhenIntersectionEmpty()
    {
        using var db = CreateDb();
        // Seed caller client allowed scopes = ["write"]
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboAllowedScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "write" })
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        var subject = jwt.CreateJwt("https://issuer", "api", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10));

        var result = await svc.ExchangeTokenAsync(subject, null, null, "api", new[] { "read" }, "caller-app", "https://issuer", null);
        Assert.IsFalse(result.ok);
        Assert.AreEqual(400, result.status);
        Assert.AreEqual("insufficient_scope", result.error);
    }

    [TestMethod]
    public async Task TokenExchange_Lifetime_Capped_ByPolicy_MinOfSubjectRemainingAndPolicy()
    {
        using var db = CreateDb();
        // Seed caller client with lifetime cap 3 minutes
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboMaxLifetimeMinutes = 3
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        // Subject with 10 minutes remaining, requested read scope allowed by default
        var subject = jwt.CreateJwt("https://issuer", "api", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10));

        var result = await svc.ExchangeTokenAsync(subject, null, null, "api", new[] { "read" }, "caller-app", "https://issuer", null);
        Assert.IsTrue(result.ok);
        Assert.AreEqual(200, result.status);
        // expires_in should be <= 180 seconds (cap 3 minutes)
        var json = System.Text.Json.JsonSerializer.Serialize(result.payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var exp = doc.RootElement.GetProperty("expires_in").GetInt32();
        Assert.IsTrue(exp <= 180 && exp > 0);
    }
}
