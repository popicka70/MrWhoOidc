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

        // RFC 8693 §4.1: act is a JSON object in the token, not a JSON-encoded string.
        using var rawPayload = JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(token!.Split('.')[1]));
        Assert.AreEqual(JsonValueKind.Object, rawPayload.RootElement.GetProperty("act").ValueKind);

        // The issued JWT is recorded (by hash and jti) so it can be revoked and introspected consistently.
        var issuedJti = principal.FindFirst("jti")?.Value;
        var row = await db.Tokens.SingleOrDefaultAsync(t => t.Type == "access" && t.TokenHash == MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(token));
        Assert.IsNotNull(row, "token-exchange JWT access tokens must be persisted");
        Assert.AreEqual(issuedJti, row.Jti);
        Assert.AreEqual(userId, row.UserId);
        Assert.AreEqual("caller-app", row.ClientId);
        Assert.AreEqual("api2", row.Audience);
    }

    [TestMethod]
    public async Task TokenExchange_MtlsBoundSubject_RequiresAndPropagatesMatchingCertificate()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api", "api2");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var service = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, new MockScopeResolver(), new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, null);

        var userId = Guid.NewGuid();
        const string certificateThumbprint = "subject-cert-thumb";
        var confirmation = JsonSerializer.Serialize(new Dictionary<string, string> { ["x5t#S256"] = certificateThumbprint });
        var subject = await jwt.CreateJwtAsync(
            issuer: "https://issuer",
            audience: "api",
            claims:
            [
                new Claim("sub", userId.ToString()),
                new Claim("scope", "read"),
                new Claim("cnf", confirmation, JsonClaimValueTypes.Json)
            ],
            expires: DateTimeOffset.UtcNow.AddMinutes(10));
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api", "read");

        foreach (var presentedThumbprint in new string?[] { null, "wrong-cert-thumb" })
        {
            var rejected = await service.ExchangeTokenAsync(
                subjectToken: subject,
                subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
                requestedTokenType: null,
                requestedAudience: "api2",
                requestedScopes: ["read"],
                callerClientId: "caller-app",
                issuer: "https://issuer",
                dpopJkt: null,
                mtlsX5tS256: presentedThumbprint);

            Assert.IsFalse(rejected.ok);
            Assert.AreEqual(400, rejected.status);
            Assert.AreEqual("invalid_grant", rejected.error);
        }

        var accepted = await service.ExchangeTokenAsync(
            subjectToken: subject,
            subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
            requestedTokenType: null,
            requestedAudience: "api2",
            requestedScopes: ["read"],
            callerClientId: "caller-app",
            issuer: "https://issuer",
            dpopJkt: null,
            mtlsX5tS256: certificateThumbprint);

        Assert.IsTrue(accepted.ok);
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(accepted.payload));
        var exchangedToken = response.RootElement.GetProperty("access_token").GetString()!;
        using var payload = JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(exchangedToken.Split('.')[1]));
        Assert.AreEqual(certificateThumbprint, payload.RootElement.GetProperty("cnf").GetProperty("x5t#S256").GetString());
        var issuedRow = await db.Tokens.SingleAsync(token => token.TokenHash == MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(exchangedToken));
        Assert.AreEqual(certificateThumbprint, issuedRow.CnfX5tS256);
    }

    private static MrWhoOidc.Auth.Persistence.Client PairwiseClient(string clientId, string redirectHost) => new()
    {
        ClientId = clientId,
        SubjectType = MrWhoOidc.Auth.Protocols.OidcConstants.SubjectTypes.Pairwise,
        AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { $"https://{redirectHost}/cb" })
    };

    private static MrWhoOidc.Auth.Services.SubjectIdentifiers.PairwiseSubjectService Pairwise(AuthDbContext db)
        => new(db, new MrWhoOidc.Auth.Services.SubjectIdentifiers.SectorIdentifierResolver(new Moq.Mock<IHttpClientFactory>().Object),
            NullLogger<MrWhoOidc.Auth.Services.SubjectIdentifiers.PairwiseSubjectService>.Instance);

    private static async Task<(bool ok, string? sub, string? error)> ExchangeAsync(
        AuthDbContext db, KeyStore keyStore, IJwtService jwt, string subjectToken, string callerClientId)
    {
        var opts = Options("api", "api2");
        var svc = new TokenExchangeService(
            db, jwt, opts, TestTokenValidatorFactory.Create(keyStore), new MockTenantSettingsService(), new MockScopeResolver(),
            new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, null, pairwiseSubjects: Pairwise(db));

        var (ok, payload, error, _) = await svc.ExchangeTokenAsync(
            subjectToken: subjectToken,
            subjectTokenType: "urn:ietf:params:oauth:token-type:access_token",
            requestedTokenType: null,
            requestedAudience: "api2",
            requestedScopes: new[] { "read" },
            callerClientId: callerClientId,
            issuer: "https://issuer",
            dpopJkt: null);
        if (!ok) return (false, null, error);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        return (true, new JwtSecurityTokenHandler().ReadJwtToken(token).Subject, null);
    }

    // R4: a pairwise client's access token (opaque pairwise sub) is a valid subject_token, and the
    // exchanged token carries the caller's pairwise sub instead of the internal user id.
    [TestMethod]
    public async Task TokenExchange_PairwiseSubjectToken_IsAccepted_AndIssuesPairwiseSub()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var caller = PairwiseClient("pw-app", "pw.example.com");
        db.Clients.Add(caller);
        await db.SaveChangesAsync();

        var userId = Guid.NewGuid();
        var pairwiseSub = await Pairwise(db).GetSubjectAsync(caller, userId);
        var subject = await jwt.CreateJwtAsync("https://issuer", "api",
            new[] { new Claim("sub", pairwiseSub), new Claim("scope", "read write") }, DateTimeOffset.UtcNow.AddMinutes(10));
        await PersistJwtSubjectAsync(db, subject, userId, "pw-app", "api", "read", "write");

        var (ok, sub, error) = await ExchangeAsync(db, keyStore, jwt, subject, "pw-app");

        Assert.IsTrue(ok, error);
        Assert.AreEqual(pairwiseSub, sub);
        Assert.AreNotEqual(userId.ToString(), sub);
    }

    [TestMethod]
    public async Task TokenExchange_PublicSubjectToken_ForPairwiseCaller_IssuesCallersPairwiseSub()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var caller = PairwiseClient("pw-gateway", "gw.example.com");
        caller.OboAllowedCallersJson = JsonSerializer.Serialize(new[] { "spa" });
        caller.OboAllowedSourceAudiencesJson = JsonSerializer.Serialize(new[] { "api" });
        db.Clients.Add(caller);
        await db.SaveChangesAsync();

        var userId = Guid.NewGuid();
        var subject = await jwt.CreateJwtAsync("https://issuer", "api",
            new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10));
        await PersistJwtSubjectAsync(db, subject, userId, "spa", "api", "read");

        var (ok, sub, error) = await ExchangeAsync(db, keyStore, jwt, subject, "pw-gateway");

        Assert.IsTrue(ok, error);
        Assert.AreNotEqual(userId.ToString(), sub, "the internal user id must not reach a pairwise client");
        Assert.AreEqual(await Pairwise(db).GetSubjectAsync(caller, userId), sub);
    }

    [TestMethod]
    public async Task TokenExchange_PairwiseSubOfAnotherUser_IsRejected()
    {
        using var db = CreateDb();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var caller = PairwiseClient("pw-app", "pw.example.com");
        db.Clients.Add(caller);
        await db.SaveChangesAsync();

        var userId = Guid.NewGuid();
        var otherUsersSub = await Pairwise(db).GetSubjectAsync(caller, Guid.NewGuid());
        var subject = await jwt.CreateJwtAsync("https://issuer", "api",
            new[] { new Claim("sub", otherUsersSub), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10));
        await PersistJwtSubjectAsync(db, subject, userId, "pw-app", "api", "read");

        var (ok, _, error) = await ExchangeAsync(db, keyStore, jwt, subject, "pw-app");

        Assert.IsFalse(ok);
        Assert.AreEqual("invalid_grant", error);
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


