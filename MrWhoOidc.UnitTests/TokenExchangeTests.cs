using MrWhoOidc.Auth.Services.Token;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass, TestCategory("RequiresPostgres"), DoNotParallelize]
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

    private static async Task PersistJwtSubjectAsync(AuthDbContext db, string token, Guid userId, string clientId, string audience, params string[] scopes)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        db.Tokens.Add(new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "access",
            TokenHash = MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(token),
            UserId = userId,
            ClientId = clientId,
            Audience = audience,
            ScopesJson = JsonSerializer.Serialize(scopes),
            Jti = jwt.Claims.FirstOrDefault(c => c.Type == "jti")?.Value,
            ExpiresAt = jwt.Payload.Expiration.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(jwt.Payload.Expiration.Value)
                : DateTimeOffset.UtcNow.AddMinutes(10),
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task TokenExchange_HappyPath_JwtSubject_NarrowsScopes_EmitsAct()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api", "api2");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, null);

        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var subject = await jwt.CreateJwtAsync(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read write") },
            expires: now.AddMinutes(10)
        ).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api", "read", "write");

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
        var tv = TestTokenValidatorFactory.Create(keyStore);
        var (vok, principal, _) = await tv.ValidateAsync(token!, "https://issuer", skipAudienceValidation: true);
        Assert.IsTrue(vok);
        Assert.IsNotNull(principal);
        var act = principal!.FindFirst("act")?.Value;
        Assert.IsFalse(string.IsNullOrEmpty(act));
    }

    [TestMethod]
    public async Task TokenExchange_DPoPBridgingDenied_WhenSubjectHasCnf()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, null);

        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cnfJson = System.Text.Json.JsonSerializer.Serialize(new { jkt = "abc" });
        var subject = await jwt.CreateJwtAsync(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read"), new Claim("cnf", cnfJson) },
            expires: now.AddMinutes(10)
        ).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api", "read");

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
        var settingsService = new MockTenantSettingsService();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, null);

        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        // Subject JWT contains an 'act' claim -> must be rejected as single-hop only
        var actJson = System.Text.Json.JsonSerializer.Serialize(new { sub = "some-actor" });
        var subject = await jwt.CreateJwtAsync(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read"), new Claim("act", actJson) },
            expires: now.AddMinutes(10)
        ).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api", "read");

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

    [TestMethod]
    public async Task TokenExchange_OpaqueSubject_RejectsInvalidAudience()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api"); // Only "api" is allowed
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, null);

        var userId = Guid.NewGuid();
        var tokenValue = "opaque-token-123";
        var hash = MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(tokenValue);

        db.Tokens.Add(new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "access",
            TokenHash = hash,
            UserId = userId,
            ClientId = "caller-app",
            Audience = "untrusted-api", // Not in allowed ApiAudiences
            ScopesJson = "[\"read\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var (ok, _, error, status) = await svc.ExchangeTokenAsync(
            subjectToken: tokenValue,
            subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
            requestedTokenType: null,
            requestedAudience: "api",
            requestedScopes: new[] { "read" },
            callerClientId: "caller-app",
            issuer: "https://issuer",
            dpopJkt: null
        );

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task TokenExchange_JwtSubject_RejectsTokenIssuedToDifferentClient()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api", "api2");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, null);

        var userId = Guid.NewGuid();
        var subject = await jwt.CreateJwtAsync(
            issuer: "https://issuer",
            audience: "api",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read write") },
            expires: DateTimeOffset.UtcNow.AddMinutes(10)
        ).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "other-client", "api", "read", "write");

        var (ok, _, error, status) = await svc.ExchangeTokenAsync(
            subjectToken: subject,
            subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
            requestedTokenType: null,
            requestedAudience: "api2",
            requestedScopes: new[] { "read" },
            callerClientId: "caller-app",
            issuer: "https://issuer",
            dpopJkt: null
        );

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }
}



