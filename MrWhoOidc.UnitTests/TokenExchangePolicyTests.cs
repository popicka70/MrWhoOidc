using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;

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
        var callerClient = new Client { ClientId = "caller-app", RealmId = Guid.NewGuid(), OboDpopMode = OboDpopMode.RequireSameJkt };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db);
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db);
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
        var callerClient = new Client { ClientId = "caller-app", RealmId = Guid.NewGuid(), OboMaxDelegationDepth = 1 };
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

        var keyStore = new KeyStore(db);
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db);
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
}
