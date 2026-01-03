using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public sealed class AuthorizationCodeExchangerTests
{
    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static IOptions<AuthOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions());

    private static IOptions<AuthOptions> Options(Action<AuthOptions> configure)
    {
        var opts = new AuthOptions();
        configure(opts);
        return Microsoft.Extensions.Options.Options.Create(opts);
    }

    [TestMethod]
    public async Task ExchangeAsync_Fails_ForInvalidCode()
    {
        using var db = CreateDb();
        var jwtSvc = new Mock<IJwtService>();
        var refreshSvc = new Mock<IRefreshTokenService>();
        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db, jwtSvc.Object, refreshSvc.Object, revocationSvc.Object, Options(), metaStore, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()), logger.Object);

        var request = new AuthorizationCodeExchangeRequest("bad", "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_JwtAccess()
    {
        using var db = CreateDb();
        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db, jwtSvc.Object, refreshSvc.Object, revocationSvc.Object, Options(), metaStore, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()), logger.Object);

        var code = "code123";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        
        db.AuthorizationCodes.Add(new AuthorizationCode 
        { 
            Code = code, 
            UserId = userId, 
            ClientId = "c1", 
            RedirectUri = "https://cb", 
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        
        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        Assert.AreEqual("jwt-at", dict["access_token"]);
        Assert.AreEqual("rt", dict["refresh_token"]);
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_When_ClaimsRequest_Essential_AuthTime()
    {
        using var db = CreateDb();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc
            .Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder
            .Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-auth-time";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        // Request auth_time as an essential id_token claim via the OIDC claims parameter.
        // auth_time is emitted by JwtService via the authTime parameter, not as an explicit idClaims entry.
        var claimsJson = "{\"id_token\":{\"auth_time\":{\"essential\":true}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = claimsJson
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        Assert.AreEqual("jwt", dict["access_token"]);
        Assert.AreEqual("jwt", dict["id_token"]);
        Assert.AreEqual("rt", dict["refresh_token"]);
    }

    [TestMethod]
    public async Task ExchangeAsync_Fails_When_ClaimsRequest_Essential_Acr_ValueMismatch()
    {
        using var db = CreateDb();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc
            .Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder
            .Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-acr-essential";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        // Request a specific ACR value as essential in the id_token.
        var claimsJson = "{\"id_token\":{\"acr\":{\"essential\":true,\"value\":\"urn:acr:good\"}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = claimsJson
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);
        metaStore.SetUpstream(code, idp: "urn:idp:test", acr: "urn:acr:bad", amr: "pwd");

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_request", error);
        Assert.IsNotNull(payload);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        Assert.AreEqual("invalid_request", dict["error"]);
        Assert.IsTrue(((string)dict["error_description"]!).Contains("acr", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExchangeAsync_When_RestrictIdTokenClaimsToClaimsRequest_Omits_Unrequested_Payload_Claims()
    {
        using var db = CreateDb();

        List<Claim>? capturedIdTokenClaims = null;

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc
            .Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>((issuer, aud, claims, exp, nonce, atHash, authTime, tokenType, _) =>
            {
                // ID token call path uses tokenType = null.
                if (string.IsNullOrWhiteSpace(tokenType))
                {
                    capturedIdTokenClaims = claims.ToList();
                }
            })
            .ReturnsAsync((string issuer, string aud, IEnumerable<Claim> claims, DateTimeOffset exp, string? nonce, string? atHash, DateTimeOffset? authTime, string? tokenType, CancellationToken _) =>
                string.IsNullOrWhiteSpace(tokenType) ? "jwt-id" : "jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder
            .Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(o => o.RestrictIdTokenClaimsToClaimsRequest = true),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-restrict-idclaims";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", Name = "User One", Email = "u1@example.com", EmailVerified = true, TenantId = tenantId });

        // Request auth_time only (plus required sub); with restriction enabled, scope-based payload claims are omitted.
        var claimsJson = "{\"id_token\":{\"auth_time\":{\"essential\":true}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "profile", "email", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = claimsJson
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);

        Assert.IsNotNull(capturedIdTokenClaims);
        Assert.IsTrue(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Subject));
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Name));
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Email));
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.EmailVerified));
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Realm));
    }

    [TestMethod]
    public async Task ExchangeAsync_When_RestrictIdTokenClaimsToClaimsRequest_Keeps_Explicitly_Requested_Claim()
    {
        using var db = CreateDb();

        List<Claim>? capturedIdTokenClaims = null;

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc
            .Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>((issuer, aud, claims, exp, nonce, atHash, authTime, tokenType, _) =>
            {
                if (string.IsNullOrWhiteSpace(tokenType))
                {
                    capturedIdTokenClaims = claims.ToList();
                }
            })
            .ReturnsAsync((string issuer, string aud, IEnumerable<Claim> claims, DateTimeOffset exp, string? nonce, string? atHash, DateTimeOffset? authTime, string? tokenType, CancellationToken _) =>
                string.IsNullOrWhiteSpace(tokenType) ? "jwt-id" : "jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder
            .Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(o => o.RestrictIdTokenClaimsToClaimsRequest = true),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-restrict-idclaims-email";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", Name = "User One", Email = "u1@example.com", EmailVerified = true, TenantId = tenantId });

        // Explicitly request the email claim.
        var claimsJson = "{\"id_token\":{\"email\":null}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "profile", "email", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = claimsJson
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);

        Assert.IsNotNull(capturedIdTokenClaims);
        Assert.IsTrue(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Subject));
        Assert.IsTrue(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Email));
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.EmailVerified));
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_When_ClaimsRequest_NonEssential_Acr_ValueMismatch_OmitsAcr()
    {
        using var db = CreateDb();

        var capturedClaimTypes = new List<HashSet<string>>();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc
            .Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>(
                (issuer, audience, claims, expiresAt, kid, nonce, authTime, atHash, ct) =>
                {
                    capturedClaimTypes.Add(claims.Select(c => c.Type).ToHashSet(StringComparer.Ordinal));
                })
            .ReturnsAsync("jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder
            .Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-acr-nonessential";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        // Request a specific ACR value as non-essential in the id_token.
        var claimsJson = "{\"id_token\":{\"acr\":{\"essential\":false,\"value\":\"urn:acr:good\"}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = claimsJson
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);
        metaStore.SetUpstream(code, idp: "urn:idp:test", acr: "urn:acr:bad", amr: "pwd");

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        Assert.AreEqual("jwt", dict["id_token"]);

        // Ensure we never emit `acr` when the non-essential constraint cannot be satisfied.
        Assert.IsFalse(capturedClaimTypes.Any(set => set.Contains(OidcConstants.Claims.Acr)));
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_When_ClaimsRequest_Essential_Amr_Values_Satisfied()
    {
        using var db = CreateDb();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc
            .Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder
            .Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-amr-essential-ok";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        // Request amr as essential, and require that it includes "mfa".
        var claimsJson = "{\"id_token\":{\"amr\":{\"essential\":true,\"values\":[\"mfa\"]}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = claimsJson
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);
        metaStore.SetUpstream(code, idp: "urn:idp:test", acr: "urn:acr:pwd", amr: "pwd mfa");

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
    }

    [TestMethod]
    public async Task ExchangeAsync_Fails_When_ClaimsRequest_Essential_Amr_Values_Mismatch()
    {
        using var db = CreateDb();

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc
            .Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder
            .Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-amr-essential-fail";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        // Require an amr value that will not be present.
        var claimsJson = "{\"id_token\":{\"amr\":{\"essential\":true,\"values\":[\"sms\"]}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = claimsJson
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);
        metaStore.SetUpstream(code, idp: "urn:idp:test", acr: "urn:acr:pwd", amr: "pwd mfa");

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_request", error);
        Assert.IsNotNull(payload);
    }
}
