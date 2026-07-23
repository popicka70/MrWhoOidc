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
using Moq;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Services.Delegation;

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

    private static async Task PersistJwtSubjectAsync(AuthDbContext db, string token, Guid userId, string clientId, string audience, params string[] scopes)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        db.Tokens.Add(new Token
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
    public async Task TokenExchange_DPoP_SameKey_Required_ByPolicy()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        // Seed caller client with OboDpopMode.RequireSameJkt
        var callerClient = new ClientEntity { ClientId = "caller-app", RealmId = Guid.NewGuid(), OboDpopMode = OboDpopMode.RequireSameJkt };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, new OboPolicyService(db, opts));

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
        var settingsService = new MockTenantSettingsService();
        // Seed caller client with max depth = 1
        var callerClient = new ClientEntity { ClientId = "caller-app", RealmId = Guid.NewGuid(), OboMaxDelegationDepth = 1 };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        // Seed an opaque subject token with DelegationDepth = 1 (already used once)
        var userId = Guid.NewGuid();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var hash = MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(raw);
        db.Tokens.Add(new Token
        {
            Type = "access",
            TokenHash = hash,
            UserId = userId,
            ClientId = "caller-app",
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "read" }),
            Audience = "api",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            DelegationDepth = 1
        });
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, new OboPolicyService(db, opts));

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
        var settingsService = new MockTenantSettingsService();
        // Seed caller client with allowed target audiences = ["api-b"] only
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboAllowedTargetAudiencesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "api-b" })
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api-a", "api-b");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        var subject = await jwt.CreateJwtAsync("https://issuer", "api-a", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10)).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api-a", "read");

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
        var settingsService = new MockTenantSettingsService();
        // Seed caller client allowing only source audience = api-x
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboAllowedSourceAudiencesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "api-x" })
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api-a", "api-b");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        // Subject from api-a (not allowed as source)
        var subject = await jwt.CreateJwtAsync("https://issuer", "api-a", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10)).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api-a", "read");

        var result = await svc.ExchangeTokenAsync(subject, null, null, "api-b", new[] { "read" }, "caller-app", "https://issuer", null);
        Assert.IsFalse(result.ok);
        Assert.AreEqual(400, result.status);
        Assert.AreEqual("invalid_grant", result.error);
    }

    [TestMethod]
    public async Task TokenExchange_Allows_CrossClient_Subject_WhenPolicyAllows()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        var callerClient = new ClientEntity
        {
            ClientId = "api-client",
            RealmId = Guid.NewGuid(),
            OboAllowedSourceAudiencesJson = JsonSerializer.Serialize(new[] { "frontend-app" }),
            OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { "api" }),
            OboAllowedScopesJson = JsonSerializer.Serialize(new[] { "read" })
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        var subject = await jwt.CreateJwtAsync(
            issuer: "https://issuer",
            audience: "frontend-app",
            claims: new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") },
            expires: DateTimeOffset.UtcNow.AddMinutes(10)
        ).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "frontend-app", "frontend-app", "read");

        var result = await svc.ExchangeTokenAsync(subject, null, null, "api", new[] { "read" }, "api-client", "https://issuer", null);
        Assert.IsTrue(result.ok);
        Assert.AreEqual(200, result.status);
    }

    [TestMethod]
    public async Task TokenExchange_Insufficient_Scope_WhenIntersectionEmpty()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        // Seed caller client allowed scopes = ["write"]
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboAllowedScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "write" })
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        var subject = await jwt.CreateJwtAsync("https://issuer", "api", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10)).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api", "read");

        var result = await svc.ExchangeTokenAsync(subject, null, null, "api", new[] { "read" }, "caller-app", "https://issuer", null);
        Assert.IsFalse(result.ok);
        Assert.AreEqual(400, result.status);
        Assert.AreEqual("insufficient_scope", result.error);
    }

    [TestMethod]
    public async Task TokenExchange_Lifetime_Capped_ByPolicy_MinOfSubjectRemainingAndPolicy()
    {
        using var db = CreateDb();
        var settingsService = new MockTenantSettingsService();
        // Seed caller client with lifetime cap 3 minutes
        var callerClient = new ClientEntity
        {
            ClientId = "caller-app",
            RealmId = Guid.NewGuid(),
            OboMaxLifetimeMinutes = 3
        };
        db.Clients.Add(callerClient);
        await db.SaveChangesAsync();

        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var opts = Options("api");
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var scopeResolver = new MockScopeResolver();
        var svc = new TokenExchangeService(
            db, jwt, opts, validator, settingsService, scopeResolver, new OpaqueTokenPolicy(opts), NullLogger<TokenExchangeService>.Instance, new OboPolicyService(db, opts));

        var userId = Guid.NewGuid();
        // Subject with 10 minutes remaining, requested read scope allowed by default
        var subject = await jwt.CreateJwtAsync("https://issuer", "api", new[] { new Claim("sub", userId.ToString()), new Claim("scope", "read") }, DateTimeOffset.UtcNow.AddMinutes(10)).ConfigureAwait(false);
        await PersistJwtSubjectAsync(db, subject, userId, "caller-app", "api", "read");

        var result = await svc.ExchangeTokenAsync(subject, null, null, "api", new[] { "read" }, "caller-app", "https://issuer", null);
        Assert.IsTrue(result.ok);
        Assert.AreEqual(200, result.status);
        // expires_in should be <= 180 seconds (cap 3 minutes)
        var json = System.Text.Json.JsonSerializer.Serialize(result.payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var exp = doc.RootElement.GetProperty("expires_in").GetInt32();
        Assert.IsTrue(exp <= 180 && exp > 0);
    }

    [TestMethod]
    public async Task TokenExchange_DelegatedGrant_RequiresBoundClientAndEmitsDualIdentity()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var delegateUserId = Guid.NewGuid();
        var delegateAccountId = Guid.NewGuid();
        var delegatorAccountId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "delegated-token",
            Name = "Delegated Token",
            IssuerUri = "https://issuer",
            Status = TenantStatus.Active
        });
        db.Realms.Add(new Realm { Id = realmId, TenantId = tenantId, Name = "default" });
        db.Clients.Add(new ClientEntity
        {
            Id = clientId,
            TenantId = tenantId,
            RealmId = realmId,
            ClientId = "delegated-client",
            OboEnabled = true,
            OboAllowedSourceAudiencesJson = JsonSerializer.Serialize(new[] { "api" }),
            OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { "api" }),
            OboAllowedScopesJson = JsonSerializer.Serialize(new[] { "profile" })
        });
        db.Users.Add(new User
        {
            Id = delegateUserId,
            TenantId = tenantId,
            Username = "delegate",
            Email = "delegate@example.test",
            NormalizedEmail = "DELEGATE@EXAMPLE.TEST"
        });
        db.UserAccounts.AddRange(
            new UserAccount
            {
                Id = delegateAccountId,
                Username = "delegate",
                Email = "delegate@example.test",
                NormalizedEmail = "DELEGATE@EXAMPLE.TEST"
            },
            new UserAccount { Id = delegatorAccountId, Username = "delegator" });
        db.UserTenantMemberships.AddRange(
            new UserTenantMembership { UserAccountId = delegateAccountId, TenantId = tenantId },
            new UserTenantMembership { UserAccountId = delegatorAccountId, TenantId = tenantId });
        var grant = new DelegatedAccessGrant
        {
            TenantId = tenantId,
            ClientId = clientId,
            DelegatorUserAccountId = delegatorAccountId,
            DelegateUserAccountId = delegateAccountId,
            Status = DelegatedAccessGrantStatus.Active,
            CapabilitiesJson = "[\"profile.read\"]",
            ResourceConstraintsJson = $"{{\"profile.read\":{{\"allowedTypes\":[\"user\"],\"allowedIds\":[\"{delegatorAccountId}\"]}}}}",
            Purpose = "Delegated token demo",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            AcceptanceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            StartsAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        db.DelegatedAccessGrants.Add(grant);
        await db.SaveChangesAsync();

        var settingsService = new MockTenantSettingsService();
        var keyStore = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant(), new TestHybridCache(), Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));
        var jwt = TestJwtServiceFactory.Create(keyStore);
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            ApiAudiences = ["api"],
            EnableTokenExchange = true,
            EnableDelegatedAccess = true
        });
        var validator = TestTokenValidatorFactory.Create(keyStore);
        var delegatedAuthorization = new DelegatedAccessAuthorizationService(
            db,
            new DelegableCapabilityCatalog(),
            new UserTenantMembershipService(db),
            new NoopAuditSink(),
            options,
            NullLogger<DelegatedAccessAuthorizationService>.Instance);
        var service = new TokenExchangeService(
            db,
            jwt,
            options,
            validator,
            settingsService,
            new MockScopeResolver(),
            new OpaqueTokenPolicy(options),
            NullLogger<TokenExchangeService>.Instance,
            new OboPolicyService(db, options),
            new ScopeMapper(),
            delegatedAuthorization);
        var subject = await jwt.CreateJwtAsync(
            "https://issuer",
            "api",
            [new Claim("sub", delegateUserId.ToString()), new Claim("scope", "profile")],
            DateTimeOffset.UtcNow.AddMinutes(10));
        await PersistJwtSubjectAsync(db, subject, delegateUserId, "delegated-client", "api", "profile");

        var result = await service.ExchangeTokenAsync(
            subject,
            null,
            null,
            "api",
            ["profile"],
            "delegated-client",
            "https://issuer",
            null,
            grant.Id);

        Assert.IsTrue(result.ok);
        var payloadJson = JsonSerializer.Serialize(result.payload);
        using var payloadDocument = JsonDocument.Parse(payloadJson);
        var accessToken = payloadDocument.RootElement.GetProperty("access_token").GetString();
        Assert.IsNotNull(accessToken);
        var issued = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        Assert.AreEqual(delegatorAccountId.ToString(), issued.Subject);
        Assert.AreEqual(grant.Id.ToString(), issued.Claims.Single(claim => claim.Type == "delegation_id").Value);
        Assert.AreEqual("delegated-client", issued.Claims.Single(claim => claim.Type == "client_id").Value);
        using var actorDocument = JsonDocument.Parse(issued.Claims.Single(claim => claim.Type == "act").Value);
        Assert.AreEqual(delegateAccountId.ToString(), actorDocument.RootElement.GetProperty("sub").GetString());
    }

    [TestMethod]
    public async Task TokenExchange_DelegatedGrant_WrongClientIsHidden()
    {
        using var db = CreateDb();
        var grant = new DelegatedAccessGrant
        {
            TenantId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            DelegatorUserAccountId = Guid.NewGuid(),
            DelegateUserAccountId = Guid.NewGuid(),
            Status = DelegatedAccessGrantStatus.Active,
            CapabilitiesJson = "[\"profile.read\"]",
            Purpose = "Wrong client",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            AcceptanceExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        db.DelegatedAccessGrants.Add(grant);
        db.Clients.Add(new ClientEntity { Id = Guid.NewGuid(), ClientId = "wrong-client", RealmId = Guid.NewGuid() });
        var delegateUserId = Guid.NewGuid();
        var raw = "delegated-wrong-client-subject";
        db.Tokens.Add(new Token
        {
            Type = "access",
            TokenHash = MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Base64(raw),
            UserId = delegateUserId,
            ClientId = "wrong-client",
            Audience = "api",
            ScopesJson = "[\"profile\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync();

        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            ApiAudiences = ["api"],
            EnableTokenExchange = true,
            EnableDelegatedAccess = true
        });
        var service = new TokenExchangeService(
            db,
            Mock.Of<IJwtService>(),
            options,
            Mock.Of<ITokenValidator>(),
            new MockTenantSettingsService(),
            new MockScopeResolver(),
            new OpaqueTokenPolicy(options),
            NullLogger<TokenExchangeService>.Instance,
            null,
            new ScopeMapper());

        var result = await service.ExchangeTokenAsync(raw, null, null, "api", ["profile"], "wrong-client", "https://issuer", null, grant.Id);

        Assert.IsFalse(result.ok);
        Assert.AreEqual(404, result.status);
        Assert.AreEqual("delegation_not_found", result.error);
    }
}




