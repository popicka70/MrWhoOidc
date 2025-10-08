using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass, TestCategory("RequiresPostgres")]
public sealed class TokenExchangeTests
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
    public async Task TokenExchange_HappyPath_JwtSubject_NarrowsScopes_EmitsAct()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api", "api2");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var settingsService = new MockTenantSettingsService();
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, settingsService, null);

        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var subject = jwt.CreateJwt(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read write") },
            expires: now.AddMinutes(10)
        );

        var (ok, payload, _, status) = await svc.ExchangeTokenAsync(
            subjectToken: subject,
            subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
            requestedTokenType: null,
            requestedAudience: "api2",
            requestedScopes: new[] { "read" },
            callerClientId: "caller-app",
            issuer: "https://issuer",
            dpopJkt: null
        );

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNotNull(payload);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.IsTrue(doc.RootElement.TryGetProperty("access_token", out var at));
        var token = at.GetString();
        Assert.IsFalse(string.IsNullOrEmpty(token));

        // Validate token and check for 'act' and audience
        var tv = new TokenValidator(keyStore);
        var (vok, principal, _) = tv.Validate(token!, "https://issuer");
        Assert.IsTrue(vok);
        Assert.IsNotNull(principal);
        var act = principal!.FindFirst("act")?.Value;
        Assert.IsFalse(string.IsNullOrEmpty(act));
    }

    [TestMethod]
    public async Task TokenExchange_DPoPBridgingDenied_WhenSubjectHasCnf()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var settingsService = new MockTenantSettingsService();
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, settingsService, null);

        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cnfJson = System.Text.Json.JsonSerializer.Serialize(new { jkt = "abc" });
        var subject = jwt.CreateJwt(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read"), new Claim("cnf", cnfJson) },
            expires: now.AddMinutes(10)
        );

        var (ok, payload, error, status) = await svc.ExchangeTokenAsync(
            subjectToken: subject,
            subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
            requestedTokenType: null,
            requestedAudience: null,
            requestedScopes: Array.Empty<string>(),
            callerClientId: "caller-app",
            issuer: "https://issuer",
            dpopJkt: null
        );

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_request", error);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("dpop_bridging_not_supported", doc.RootElement.GetProperty("error_description").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_SingleHop_Rejected_WhenActPresent()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());
        var jwt = new JwtService(keyStore);
        var refresh = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());
        var opts = Options("api");
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var validator = new TokenValidator(keyStore);
        var settingsService = new MockTenantSettingsService();
        var svc = new TokenService(db, jwt, refresh, opts, meta, validator, settingsService, null);

        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        // Subject JWT contains an 'act' claim -> must be rejected as single-hop only
        var actJson = System.Text.Json.JsonSerializer.Serialize(new { sub = "some-actor" });
        var subject = jwt.CreateJwt(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read"), new Claim("act", actJson) },
            expires: now.AddMinutes(10)
        );

        var (ok, payload, error, status) = await svc.ExchangeTokenAsync(
            subjectToken: subject,
            subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
            requestedTokenType: null,
            requestedAudience: null,
            requestedScopes: Array.Empty<string>(),
            callerClientId: "caller-app",
            issuer: "https://issuer",
            dpopJkt: null
        );

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("single_hop_only", doc.RootElement.GetProperty("error_description").GetString());
    }
}
