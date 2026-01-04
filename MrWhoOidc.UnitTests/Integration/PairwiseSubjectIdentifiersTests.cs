using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
public sealed class PairwiseSubjectIdentifiersTests
{
    private static AuthDbContext CreateDb(string name)
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static ICachedKeyProvider CreateKeyProvider()
    {
        var mock = new Mock<ICachedKeyProvider>();
        mock
            .Setup(p => p.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SymmetricSecurityKey(new byte[32]));
        return mock.Object;
    }

    private static IOptions<AuthOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions());

    [TestMethod]
    public async Task PairwiseClient_ReceivesStableSub_InIdToken()
    {
        var dbName = $"PairwiseSubjectIdentifiersTests_{Guid.NewGuid()}";
        using var db = CreateDb(dbName);

        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app.example.com/signin-oidc" })
        });
        await db.SaveChangesAsync();

        var jwtSvc = new RecordingJwtService();
        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        })));

        var resolver = new SectorIdentifierResolver(httpClientFactory);
        var pairwise = new PairwiseSubjectService(db, resolver, NullLogger<PairwiseSubjectService>.Instance);

        var keyStore = new KeyStore(
            db,
            MockTenantAccessor.CreateWithTenant(tenantId, "t1"),
            new MrWhoOidc.UnitTests.Helpers.TestHybridCache(),
            Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions()));

        var keyProvider = MrWhoOidc.UnitTests.Helpers.TestCachedKeyProviderFactory.Create(keyStore);

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc,
            keyProvider,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwise,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            NullLogger<AuthorizationCodeExchanger>.Instance);

        // Exchange #1
        var code1 = "code1";
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code1,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code1, DateTimeOffset.UtcNow);

        var req1 = new AuthorizationCodeExchangeRequest(code1, "https://cb", "c1", "verifier", "https://issuer");
        var (ok1, _, _, _) = await exchanger.ExchangeAsync(req1, CancellationToken.None);
        Assert.IsTrue(ok1);

        var sub1 = jwtSvc.LastIdTokenSub;
        Assert.IsFalse(string.IsNullOrWhiteSpace(sub1));
        Assert.AreNotEqual(userId.ToString(), sub1, "Pairwise client should not get public sub.");

        // Exchange #2 (new auth code)
        var code2 = "code2";
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code2,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code2, DateTimeOffset.UtcNow);

        var req2 = new AuthorizationCodeExchangeRequest(code2, "https://cb", "c1", "verifier", "https://issuer");
        var (ok2, _, _, _) = await exchanger.ExchangeAsync(req2, CancellationToken.None);
        Assert.IsTrue(ok2);

        var sub2 = jwtSvc.LastIdTokenSub;
        Assert.AreEqual(sub1, sub2, "Pairwise sub should be stable across logins for same user+sector.");

        // Sanity: show expected mapping service output is stable too
        var expectedSub = await pairwise.GetSubjectAsync(await db.Clients.AsNoTracking().FirstAsync(c => c.ClientId == "c1"), userId);
        Assert.AreEqual(expectedSub, sub2);
    }

    [TestMethod]
    public async Task PublicClient_ReceivesPublicSub_InIdToken()
    {
        var dbName = $"PairwiseSubjectIdentifiersTests_{Guid.NewGuid()}";
        using var db = CreateDb(dbName);

        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Public,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app.example.com/signin-oidc" })
        });
        await db.SaveChangesAsync();

        var jwtSvc = new RecordingJwtService();
        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        })));

        var resolver = new SectorIdentifierResolver(httpClientFactory);
        var pairwise = new PairwiseSubjectService(db, resolver, NullLogger<PairwiseSubjectService>.Instance);

        var keyProvider = CreateKeyProvider();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc,
            keyProvider,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwise,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            NullLogger<AuthorizationCodeExchanger>.Instance);

        var code = "code1";
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var req = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, _, _) = await exchanger.ExchangeAsync(req, CancellationToken.None);
        Assert.IsTrue(ok);

        Assert.AreEqual(userId.ToString(), jwtSvc.LastIdTokenSub);
    }

    [TestMethod]
    public async Task SameSectorIdentifierUri_YieldsSameSubAcrossTwoClients()
    {
        var dbName = $"PairwiseSubjectIdentifiersTests_{Guid.NewGuid()}";
        using var db = CreateDb(dbName);

        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        var sectorUri = "https://sector.example.com/redirect_uris.json";
        var c1Redirect = "https://app1.example.com/signin-oidc";
        var c2Redirect = "https://app2.example.com/signin-oidc";

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            SectorIdentifierUri = sectorUri,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { c1Redirect })
        });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "c2",
            RealmId = realmId,
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            SectorIdentifierUri = sectorUri,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { c2Redirect })
        });
        await db.SaveChangesAsync();

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new[] { c1Redirect, c2Redirect }))
        })));

        var resolver = new SectorIdentifierResolver(httpClientFactory);
        var pairwise = new PairwiseSubjectService(db, resolver, NullLogger<PairwiseSubjectService>.Instance);

        var jwtSvc = new RecordingJwtService();
        var exchanger = CreateExchanger(db, jwtSvc, pairwise);

        // Exchange for client 1
        var sub1 = await ExchangeAndCaptureSubAsync(db, exchanger, jwtSvc, userId, tenantId, "c1", "code1");
        Assert.AreNotEqual(userId.ToString(), sub1);

        // Exchange for client 2
        var sub2 = await ExchangeAndCaptureSubAsync(db, exchanger, jwtSvc, userId, tenantId, "c2", "code2");
        Assert.AreEqual(sub1, sub2);
    }

    [TestMethod]
    public async Task DifferentSectorIdentifierUri_YieldsDifferentSubAcrossTwoClients()
    {
        var dbName = $"PairwiseSubjectIdentifiersTests_{Guid.NewGuid()}";
        using var db = CreateDb(dbName);

        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        var c1SectorUri = "https://sector-a.example.com/redirect_uris.json";
        var c2SectorUri = "https://sector-b.example.com/redirect_uris.json";
        var c1Redirect = "https://app1.example.com/signin-oidc";
        var c2Redirect = "https://app2.example.com/signin-oidc";

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            SectorIdentifierUri = c1SectorUri,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { c1Redirect })
        });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "c2",
            RealmId = realmId,
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            SectorIdentifierUri = c2SectorUri,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { c2Redirect })
        });
        await db.SaveChangesAsync();

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(new StaticResponseHandler(req =>
        {
            var uri = req.RequestUri?.ToString();
            if (string.Equals(uri, c1SectorUri, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { c1Redirect })) };
            }
            if (string.Equals(uri, c2SectorUri, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { c2Redirect })) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));

        var resolver = new SectorIdentifierResolver(httpClientFactory);
        var pairwise = new PairwiseSubjectService(db, resolver, NullLogger<PairwiseSubjectService>.Instance);

        var jwtSvc = new RecordingJwtService();
        var exchanger = CreateExchanger(db, jwtSvc, pairwise);

        var sub1 = await ExchangeAndCaptureSubAsync(db, exchanger, jwtSvc, userId, tenantId, "c1", "code1");
        var sub2 = await ExchangeAndCaptureSubAsync(db, exchanger, jwtSvc, userId, tenantId, "c2", "code2");

        Assert.AreNotEqual(sub1, sub2);
    }

    [TestMethod]
    public async Task UnreachableSectorIdentifierUri_FailsIssuance_NoFallback()
    {
        var dbName = $"PairwiseSubjectIdentifiersTests_{Guid.NewGuid()}";
        using var db = CreateDb(dbName);

        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        var sectorUri = "https://sector.example.com/redirect_uris.json";
        var redirect = "https://app1.example.com/signin-oidc";

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            SubjectType = OidcConstants.SubjectTypes.Pairwise,
            SectorIdentifierUri = sectorUri,
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { redirect })
        });
        await db.SaveChangesAsync();

        var httpClientFactory = new StubHttpClientFactory(new HttpClient(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        var resolver = new SectorIdentifierResolver(httpClientFactory);
        var pairwise = new PairwiseSubjectService(db, resolver, NullLogger<PairwiseSubjectService>.Instance);

        var jwtSvc = new RecordingJwtService();
        var exchanger = CreateExchanger(db, jwtSvc, pairwise);

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = "code1",
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        await AssertThrowsAsync<Exception>(() => exchanger.ExchangeAsync(new AuthorizationCodeExchangeRequest("code1", "https://cb", "c1", "verifier", "https://issuer"), CancellationToken.None));
    }

    private static AuthorizationCodeExchanger CreateExchanger(AuthDbContext db, RecordingJwtService jwtSvc, IPairwiseSubjectService pairwise)
    {
        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("rt", "hash"));

        var revocationSvc = new Mock<IRevocationService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();

        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var keyProvider = CreateKeyProvider();

        return new AuthorizationCodeExchanger(
            db,
            jwtSvc,
            keyProvider,
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwise,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            NullLogger<AuthorizationCodeExchanger>.Instance);
    }

    private static async Task<string> ExchangeAndCaptureSubAsync(AuthDbContext db, AuthorizationCodeExchanger exchanger, RecordingJwtService jwtSvc, Guid userId, Guid tenantId, string clientId, string code)
    {
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            ClientId = clientId,
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var (ok, _, _, _) = await exchanger.ExchangeAsync(new AuthorizationCodeExchangeRequest(code, "https://cb", clientId, "verifier", "https://issuer"), CancellationToken.None);
        Assert.IsTrue(ok);

        return jwtSvc.LastIdTokenSub ?? string.Empty;
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected exception of type {typeof(TException).Name}.");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private sealed class RecordingJwtService : IJwtService
    {
        public string? LastIdTokenSub { get; private set; }

        public Task<string> CreateJwtAsync(
            string issuer,
            string audience,
            IEnumerable<Claim> claims,
            DateTimeOffset expires,
            string? nonce = null,
            string? accessTokenHash = null,
            DateTimeOffset? authTime = null,
            string? tokenType = null,
            CancellationToken ct = default)
        {
            // ID token call path in AuthorizationCodeExchanger passes tokenType = null.
            if (string.IsNullOrWhiteSpace(tokenType))
            {
                LastIdTokenSub = claims.FirstOrDefault(c => c.Type == OidcConstants.Claims.Subject)?.Value;
            }

            // Return any non-empty string; tests inspect captured claims.
            return Task.FromResult(tokenType == SecurityConstants.JwtTokenTypes.AtJwt ? "jwt-at" : "jwt-id");
        }

        public Task<string> CreateJwtEncryptedAsync(
            string issuer,
            string audience,
            IEnumerable<Claim> claims,
            DateTimeOffset expires,
            Microsoft.IdentityModel.Tokens.EncryptingCredentials encryptingCredentials,
            string? nonce = null,
            string? accessTokenHash = null,
            DateTimeOffset? authTime = null,
            string? tokenType = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for these tests.");
    }
}
