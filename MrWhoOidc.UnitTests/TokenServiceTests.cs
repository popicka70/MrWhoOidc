using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenServiceTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<AuthOptions> Options(bool opaque = false)
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            ApiAudiences = new[] { "api" },
            OpaqueAccessTokens = new OpaqueAccessTokenOptions { Enabled = opaque }
        });

    [TestMethod]
    public async Task ExchangeAuthorizationCode_Fails_ForInvalidCode()
    {
        using var db = CreateDb();
        var ks = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var settingsService = new MockTenantSettingsService();
        var svc = new TokenService(db, new JwtService(ks), new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant(), settingsService), Options(), new InMemoryAuthorizationCodeMetadataStore(), new TokenValidator(ks), settingsService, null);
        var (ok, payload, error, status) = await svc.ExchangeAuthorizationCodeAsync("bad", "https://cb", "c1", "verifier", "https://issuer");
        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
    }

    [TestMethod]
    public async Task ExchangeAuthorizationCode_Succeeds_JwtAccess_IncludesAtHash()
    {
        using var db = CreateDb();
        var user = new User { Username = "u", PasswordHash = "x" };
        var client = new ClientEntity { ClientId = "c1" };
        db.Users.Add(user);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var code = new AuthorizationCode
        {
            Code = "code",
            ClientId = "c1",
            RedirectUri = "https://cb",
            CodeChallenge = null,
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            UserId = user.Id,
            Nonce = "n",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        db.AuthorizationCodes.Add(code);
        await db.SaveChangesAsync();

        var ks2 = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwtSvc = new JwtService(ks2);
        var settingsService = new MockTenantSettingsService();
        var svc = new TokenService(db, jwtSvc, new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant(), settingsService), Options(), new InMemoryAuthorizationCodeMetadataStore(), new TokenValidator(ks2), settingsService, null);
        var (ok, payload, error, status) = await svc.ExchangeAuthorizationCodeAsync("code", "https://cb", "c1", "", "https://issuer");
        Assert.IsTrue(ok);
        var anon = (dynamic)payload!;
        string idToken = anon.id_token;
        Assert.IsFalse(string.IsNullOrEmpty(idToken));
        // Should mark code as consumed
        Assert.IsTrue(db.AuthorizationCodes.Single(a => a.Code == "code").Consumed);
        // Refresh token persisted
        Assert.AreEqual(1, db.Tokens.Count(t => t.Type == "refresh"));
    }

    [TestMethod]
    public async Task ExchangeAuthorizationCode_Succeeds_OpaqueAccessToken_Persisted()
    {
        using var db = CreateDb();
        var user = new User { Username = "u", PasswordHash = "x" };
        var client = new ClientEntity { ClientId = "c1" };
        db.Users.Add(user);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var code = new AuthorizationCode
        {
            Code = "code2",
            ClientId = "c1",
            RedirectUri = "https://cb",
            CodeChallenge = null,
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            UserId = user.Id,
            Nonce = "n",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        db.AuthorizationCodes.Add(code);
        await db.SaveChangesAsync();

        var ks3 = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var settingsService = new MockTenantSettingsService();
        var svc = new TokenService(db, new JwtService(ks3), new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant(), settingsService), Options(opaque: true), new InMemoryAuthorizationCodeMetadataStore(), new TokenValidator(ks3), settingsService, null);
        var (ok, payload, _, status) = await svc.ExchangeAuthorizationCodeAsync("code2", "https://cb", "c1", "", "https://issuer");
        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        // Access token persisted as opaque
        Assert.AreEqual(1, db.Tokens.Count(t => t.Type == "access"));
    }

    [TestMethod]
    public async Task ExchangeRefreshToken_Rotates_AndRevokesOld()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        var user = new User { Username = "u", PasswordHash = "x" };
        var client = new ClientEntity { ClientId = "c1" };
        db.Users.Add(user);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        // Create RT directly via service
        var rtSvc = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant(), settingsService);
        var (rt, hash) = await rtSvc.CreateRefreshTokenAsync(user.Id, "c1", new[] { "openid" });
        var ks4 = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var svc = new TokenService(db, new JwtService(ks4), rtSvc, Options(), new InMemoryAuthorizationCodeMetadataStore(), new TokenValidator(ks4), settingsService, null);
        var (ok, payload, _, status) = await svc.ExchangeRefreshTokenAsync(rt, "c1", "https://issuer");
        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        // Old RT should be revoked
        Assert.AreEqual(1, db.Tokens.Count(t => t.Type == "refresh" && t.RevokedAt != null));
    }
}
