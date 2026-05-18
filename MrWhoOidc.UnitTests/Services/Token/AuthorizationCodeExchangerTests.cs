using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestSupport;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Services.Token;

[TestClass]
public sealed class AuthorizationCodeExchangerTests
{
    // Cached client encryption key for JWE tests
    private static readonly RsaSecurityKey s_clientEncryptionKey = SharedTestKeys.GetRsaSecurityKeyAlt("client-enc-key");

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    private static AuthDbContext CreateSqliteDb(SqliteConnection connection)
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AuthDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private static AuthDbContext CreateSqliteDb(SqliteConnection connection, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(connection);

        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        var db = new AuthDbContext(builder.Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static IOptions<AuthOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new AuthOptions());

    private static IOptions<AuthOptions> Options(Action<AuthOptions> configure)
    {
        var opts = new AuthOptions();
        configure(opts);
        return Microsoft.Extensions.Options.Options.Create(opts);
    }

    private static ICachedKeyProvider CreateKeyProvider()
    {
        var mock = new Mock<ICachedKeyProvider>();
        mock
            .Setup(p => p.GetActiveSigningKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SymmetricSecurityKey(new byte[32]));
        return mock.Object;
    }

    private static string CreatePrivateRsaJwkJson(string alg, string use = "sig")
    {
        using var rsa = RSA.Create(2048);
        var jwk = RsaJwk.FromRSA(rsa, Guid.NewGuid().ToString("N"), alg: alg, includePrivate: true, use: use);
        return jwk.ToJson(includePrivate: true);
    }

    private static string CreatePrivateEcJwkJson(string alg)
    {
        var curve = alg.ToUpperInvariant() switch
        {
            "ES256" => ECCurve.NamedCurves.nistP256,
            "ES384" => ECCurve.NamedCurves.nistP384,
            "ES512" => ECCurve.NamedCurves.nistP521,
            _ => ECCurve.NamedCurves.nistP256
        };

        using var ecdsa = ECDsa.Create(curve);
        var jwk = EcJwk.FromECDsa(ecdsa, Guid.NewGuid().ToString("N"), alg: alg, includePrivate: true);
        return jwk.ToJson(includePrivate: true);
    }

    private static string HashAuthorizationCode(string code)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public int CommandCount => _commands.Count;

        public string DumpCommands()
            => string.Join(Environment.NewLine + "---" + Environment.NewLine, _commands);

        public void Reset()
        {
            while (_commands.TryDequeue(out _))
            {
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            _commands.Enqueue(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            _commands.Enqueue(command.CommandText);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
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
            db, jwtSvc.Object, CreateKeyProvider(), refreshSvc.Object, revocationSvc.Object, Options(), metaStore, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()), logger.Object);

        var request = new AuthorizationCodeExchangeRequest("bad", "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task ExchangeAsync_ReplayedConsumedCode_CommitsAccessTokenRevocation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateSqliteDb(connection);

        var jwtSvc = new Mock<IJwtService>();
        var refreshSvc = new Mock<IRefreshTokenService>();
        var metaStore = new InMemoryAuthorizationCodeMetadataStore();
        var tenantId = Guid.NewGuid();
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(tenantId, "default");
        var revocationSvc = new RevocationService(db, tenantAccessor);
        var settingsSvc = new MockTenantSettingsService();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            CreateKeyProvider(),
            refreshSvc.Object,
            revocationSvc,
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

        var code = "reused-code";
        var userId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://issuer"
        });

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Consumed = true,
            TenantId = tenantId
        });

        db.Tokens.Add(new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "access",
            TokenHash = CryptoHelper.ComputeSha256Base64("jwt-at"),
            UserId = userId,
            ClientId = "c1",
            TenantId = tenantId,
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            Audience = "api",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer", TenantId: tenantId);
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);

        var persistedToken = await db.Tokens.AsNoTracking().SingleAsync();
        Assert.IsNotNull(persistedToken.RevokedAt);
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_JwtAccess()
    {
        using var db = CreateDb();
        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            db, jwtSvc.Object, CreateKeyProvider(), refreshSvc.Object, revocationSvc.Object, Options(), metaStore, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()), logger.Object);

        var code = "code123";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        var storedCodeHash = HashAuthorizationCode(code);
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = storedCodeHash,
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

        var persistedAccess = await db.Tokens.SingleOrDefaultAsync(t => t.Type == "access");
        Assert.IsNotNull(persistedAccess);
        Assert.AreEqual(tenantId, persistedAccess!.TenantId);
        Assert.AreEqual(CryptoHelper.ComputeSha256Base64("jwt-at"), persistedAccess.TokenHash);
    }

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_JwtAccess_StaysWithinFiveDatabaseCommands()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commandCounter = new CommandCountingInterceptor();
        await using var db = CreateSqliteDb(connection, commandCounter);

        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
        claimBuilder.Setup(x => x.BuildClaimsAsync(It.IsAny<AccessTokenClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Claim>());

        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db, jwtSvc.Object, CreateKeyProvider(), refreshSvc.Object, revocationSvc.Object, Options(), metaStore, settingsSvc, entitlementsProvider, tenantsClaimService, pairwiseSubjectService.Object, claimBuilder.Object, new TokenLifetimeResolver(), new OpaqueTokenPolicy(Options()), logger.Object);

        var code = "code123-sqlite";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = "default",
            Name = "Default Tenant",
            IssuerUri = "https://issuer"
        });
        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);
        commandCounter.Reset();

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
        Assert.IsTrue(
            commandCounter.CommandCount <= 5,
            $"Expected at most 5 database commands during exchange, observed {commandCounter.CommandCount}.{Environment.NewLine}{commandCounter.DumpCommands()}");
    }

    [TestMethod]
    public async Task ExchangeAsync_Preserves_NonProduct_CustomScopes()
    {
        using var db = CreateDb();
        var capturedClaims = new List<Claim>();
        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Claim>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>((_, _, claims, _, _, _, _, tokenType, _) =>
            {
                if (string.Equals(tokenType, SecurityConstants.JwtTokenTypes.AtJwt, StringComparison.Ordinal))
                {
                    capturedClaims.Clear();
                    capturedClaims.AddRange(claims);
                }
            })
            .ReturnsAsync("jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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

        var claimBuilder = new AccessTokenClaimBuilder(new ScopeResolver(db), Mock.Of<IRoleClaimBuilder>(), Options());
        var logger = new Mock<ILogger<AuthorizationCodeExchanger>>();

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            CreateKeyProvider(),
            refreshSvc.Object,
            revocationSvc.Object,
            Options(),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-custom-scope";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var realmId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = realmId, Name = "r1", TenantId = tenantId });
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access", "api.read" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(200, status);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        Assert.AreEqual("openid offline_access api.read", dict["scope"]);

        var scopeClaim = capturedClaims.Single(c => c.Type == OAuthConstants.Parameters.Scope);
        Assert.AreEqual("openid offline_access api.read", scopeClaim.Value);
    }

    [TestMethod]
    public async Task ExchangeAsync_Fails_When_IdTokenSignedResponseAlg_IsUnavailable_ForTenant()
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
            .ReturnsAsync("jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
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
            CreateKeyProvider(),
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

        var code = "code-idtoken-alg-mismatch";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        // CreateKeyProvider() uses a symmetric key; no compatible ES256 tenant signing key exists.
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            IdTokenSignedResponseAlg = SecurityConstants.JwtAlgorithms.ES256
        });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
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

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_request", error);
        Assert.IsNotNull(payload);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        Assert.AreEqual("invalid_request", dict["error"]);
        Assert.IsTrue(((string)dict["error_description"]!).Contains("id_token_signed_response_alg", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExchangeAsync_Uses_Compatible_NonActive_SigningKey_For_IdToken_When_Client_Requests_It()
    {
        using var db = CreateDb();

        var tenantId = Guid.NewGuid();
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(tenantId, "default");
        var keyStore = new KeyStore(
            db,
            tenantAccessor,
            new TestHybridCache(),
            Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions { SigningAlgorithm = SecurityConstants.JwtAlgorithms.ES256 }));

        var keyProvider = TestCachedKeyProviderFactory.Create(keyStore);
        var jwtSvc = TestJwtServiceFactory.Create(keyStore);

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            jwtSvc,
            keyProvider,
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

        db.SigningKeys.Add(new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Use = "sig",
            Alg = SecurityConstants.JwtAlgorithms.RS256,
            JwkJson = CreatePrivateRsaJwkJson(SecurityConstants.JwtAlgorithms.RS256),
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        db.SigningKeys.Add(new SigningKey
        {
            Kid = Guid.NewGuid().ToString("N"),
            Use = "sig",
            Alg = SecurityConstants.JwtAlgorithms.ES256,
            JwkJson = CreatePrivateEcJwkJson(SecurityConstants.JwtAlgorithms.ES256),
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        var code = "code-rs256-overlap";
        var userId = Guid.NewGuid();

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            IdTokenSignedResponseAlg = SecurityConstants.JwtAlgorithms.RS256
        });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer", TenantId: tenantId);
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(200, status);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        var accessToken = dict["access_token"] as string;
        var idToken = dict["id_token"] as string;

        Assert.IsFalse(string.IsNullOrWhiteSpace(accessToken));
        Assert.IsFalse(string.IsNullOrWhiteSpace(idToken));

        var parsedId = new JwtSecurityTokenHandler().ReadJwtToken(idToken!);
        Assert.AreEqual(SecurityConstants.JwtAlgorithms.RS256, parsedId.Header.Alg);
        Assert.IsTrue(parsedId.Payload.TryGetValue("at_hash", out var atHashObj));
        Assert.AreEqual(
            CryptoHelper.ComputeLeftHalfHashBase64Url(accessToken!, SecurityConstants.JwtAlgorithms.RS256),
            atHashObj?.ToString());
    }

    [TestMethod]
    public async Task ExchangeAsync_ES256_IdToken_Alg_And_AtHash_Are_Correct()
    {
        using var db = CreateDb();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var tenantId = tenantAccessor.CurrentTenant!.TenantId;

        var keyStore = new KeyStore(
            db,
            tenantAccessor,
            new TestHybridCache(),
            Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions { SigningAlgorithm = SecurityConstants.JwtAlgorithms.ES256 }));

        var keyProvider = TestCachedKeyProviderFactory.Create(keyStore);
        var jwtSvc = TestJwtServiceFactory.Create(keyStore);

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            jwtSvc,
            keyProvider,
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

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        var code = "code-es256";
        var userId = Guid.NewGuid();

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        var storedCodeHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = storedCodeHash,
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        var accessToken = dict["access_token"] as string;
        var idToken = dict["id_token"] as string;

        Assert.IsFalse(string.IsNullOrWhiteSpace(accessToken));
        Assert.IsFalse(string.IsNullOrWhiteSpace(idToken));

        var handler = new JwtSecurityTokenHandler();
        var parsedId = handler.ReadJwtToken(idToken!);

        Assert.AreEqual(SecurityConstants.JwtAlgorithms.ES256, parsedId.Header.Alg);

        Assert.IsTrue(parsedId.Payload.TryGetValue("at_hash", out var atHashObj));
        var atHash = atHashObj?.ToString();
        var expectedAtHash = CryptoHelper.ComputeLeftHalfHashBase64Url(accessToken!, SecurityConstants.JwtAlgorithms.ES256);

        Assert.AreEqual(expectedAtHash, atHash);
        Assert.AreEqual(22, expectedAtHash.Length);
    }

    [TestMethod]
    public async Task ExchangeAsync_EncryptedIdToken_When_Client_Requests_Encryption()
    {
        using var db = CreateDb();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var tenantId = tenantAccessor.CurrentTenant!.TenantId;

        var keyStore = new KeyStore(
            db,
            tenantAccessor,
            new TestHybridCache(),
            Microsoft.Extensions.Options.Options.Create(new KeyRotationOptions { SigningAlgorithm = SecurityConstants.JwtAlgorithms.RS256 }));

        var keyProvider = TestCachedKeyProviderFactory.Create(keyStore);
        var jwtSvc = TestJwtServiceFactory.Create(keyStore);

        // Use shared client encryption key instead of generating a new one
        var rsaKey = s_clientEncryptionKey;
        var rsaParams = SharedTestKeys.GetRsaParametersAlt(includePrivate: false);
        var encJwk = new JsonWebKey
        {
            Kty = "RSA",
            Use = "enc",
            Kid = rsaKey.KeyId,
            N = Base64UrlEncoder.Encode(rsaParams.Modulus!),
            E = Base64UrlEncoder.Encode(rsaParams.Exponent!)
        };
        var jwksJson = $"{{\"keys\":[{{\"kty\":\"{encJwk.Kty}\",\"use\":\"{encJwk.Use}\",\"kid\":\"{encJwk.Kid}\",\"n\":\"{encJwk.N}\",\"e\":\"{encJwk.E}\"}}]}}";

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            jwtSvc,
            keyProvider,
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

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        var code = "code-idtoken-jwe";
        var userId = Guid.NewGuid();

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            PublicJwksJson = jwksJson,
            IdTokenEncryptedResponseAlg = SecurityAlgorithms.RsaOAEP,
            IdTokenEncryptedResponseEnc = SecurityAlgorithms.Aes256CbcHmacSha512
        });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var storedClient = await db.Clients.AsNoTracking().FirstAsync(c => c.ClientId == "c1");
        Assert.AreEqual(SecurityAlgorithms.RsaOAEP, storedClient.IdTokenEncryptedResponseAlg);
        Assert.AreEqual(SecurityAlgorithms.Aes256CbcHmacSha512, storedClient.IdTokenEncryptedResponseEnc);
        Assert.IsFalse(string.IsNullOrWhiteSpace(storedClient.PublicJwksJson));

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, payload, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);

        var dict = (IDictionary<string, object?>)payload!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));
        var accessToken = dict["access_token"] as string;
        var idToken = dict["id_token"] as string;

        Assert.IsFalse(string.IsNullOrWhiteSpace(accessToken));
        Assert.IsFalse(string.IsNullOrWhiteSpace(idToken));

        // Encrypted JWT (JWE compact serialization) has 5 parts.
        Assert.AreEqual(5, idToken!.Split('.').Length);

        // Decrypt and validate signature.
        var activeSigningKey = await keyProvider.GetActiveSigningKeyAsync(CancellationToken.None);

        var handler = new JwtSecurityTokenHandler();
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://issuer",
            ValidateAudience = true,
            ValidAudience = "c1",
            ValidateLifetime = false,
            IssuerSigningKey = activeSigningKey,
            TokenDecryptionKey = rsaKey
        };

        var principal = handler.ValidateToken(idToken, tvp, out _);
        var atHash = principal.FindFirst("at_hash")?.Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(atHash));

        var expectedAtHash = CryptoHelper.ComputeLeftHalfHashBase64Url(accessToken!, SecurityConstants.JwtAlgorithms.RS256);
        Assert.AreEqual(expectedAtHash, atHash);
    }

    [TestMethod]
    public async Task ExchangeAsync_Emits_AuthTime_When_Client_Requires_It()
    {
        using var db = CreateDb();

        DateTimeOffset? capturedAuthTime = null;

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
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>((_, _, _, _, _, _, authTime, tokenType, _) =>
            {
                if (string.IsNullOrWhiteSpace(tokenType))
                {
                    capturedAuthTime = authTime;
                }
            })
            .ReturnsAsync((string _, string _, IEnumerable<Claim> _, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? tokenType, CancellationToken _) =>
                string.IsNullOrWhiteSpace(tokenType) ? "jwt-id" : "jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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

        var code = "code-require-auth-time";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "c1",
            RealmId = realmId,
            TenantId = tenantId,
            RequireAuthTime = true
        });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
        Assert.IsNotNull(capturedAuthTime);
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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
            Code = HashAuthorizationCode(code),
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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
            Code = HashAuthorizationCode(code),
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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
            Code = HashAuthorizationCode(code),
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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
            Code = HashAuthorizationCode(code),
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
    public async Task ExchangeAsync_DefaultCodeFlow_Omits_Email_Claims_From_IdToken()
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
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>((_, _, claims, _, _, _, _, tokenType, _) =>
            {
                if (string.IsNullOrWhiteSpace(tokenType))
                {
                    capturedIdTokenClaims = claims.ToList();
                }
            })
            .ReturnsAsync((string _, string _, IEnumerable<Claim> _, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? tokenType, CancellationToken _) =>
                string.IsNullOrWhiteSpace(tokenType) ? "jwt-id" : "jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            CreateKeyProvider(),
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
            Mock.Of<ILogger<AuthorizationCodeExchanger>>());

        var code = "code-default-email-omit";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User
        {
            Id = userId,
            Username = "u1",
            Email = "u1@example.com",
            EmailVerified = true,
            TenantId = tenantId
        });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "email", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
        Assert.IsNotNull(capturedIdTokenClaims);
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Email));
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.EmailVerified));
    }

    [TestMethod]
    public async Task ExchangeAsync_Emits_EmailVerified_As_Boolean_IdToken_Claim_When_Explicitly_Requested()
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
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>((_, _, claims, _, _, _, _, tokenType, _) =>
            {
                if (string.IsNullOrWhiteSpace(tokenType))
                {
                    capturedIdTokenClaims = claims.ToList();
                }
            })
            .ReturnsAsync((string _, string _, IEnumerable<Claim> _, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? tokenType, CancellationToken _) =>
                string.IsNullOrWhiteSpace(tokenType) ? "jwt-id" : "jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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

        var code = "code-idtoken-email-verified";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User
        {
            Id = userId,
            Username = "u1",
            Email = "u1@example.com",
            EmailVerified = true,
            TenantId = tenantId
        });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "email", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = "{\"id_token\":{\"email_verified\":null}}"
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
        Assert.IsNotNull(capturedIdTokenClaims);

        var emailVerifiedClaim = capturedIdTokenClaims!.Single(c => c.Type == OidcConstants.Claims.EmailVerified);
        Assert.AreEqual("true", emailVerifiedClaim.Value);
        Assert.AreEqual(ClaimValueTypes.Boolean, emailVerifiedClaim.ValueType);
    }

    [TestMethod]
    public async Task ExchangeAsync_OpenIdOnly_Omits_Custom_IdToken_Claims_When_NotRequested()
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
            .Callback<string, string, IEnumerable<Claim>, DateTimeOffset, string?, string?, DateTimeOffset?, string?, CancellationToken>((_, _, claims, _, _, _, _, tokenType, _) =>
            {
                if (string.IsNullOrWhiteSpace(tokenType))
                {
                    capturedIdTokenClaims = claims.ToList();
                }
            })
            .ReturnsAsync((string _, string _, IEnumerable<Claim> _, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? tokenType, CancellationToken _) =>
                string.IsNullOrWhiteSpace(tokenType) ? "jwt-id" : "jwt-at");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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

        var exchanger = new AuthorizationCodeExchanger(
            db,
            jwtSvc.Object,
            CreateKeyProvider(),
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
            Mock.Of<ILogger<AuthorizationCodeExchanger>>());

        var code = "code-openid-only-custom-claims";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "default", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        metaStore.SetAuthTime(code, DateTimeOffset.UtcNow);
        metaStore.SetUpstream(code, idp: "local", acr: OidcConstants.AcrValues.Password, amr: "pwd");

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
        Assert.IsNotNull(capturedIdTokenClaims);
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Idp));
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == OidcConstants.Claims.Realm));
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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
            Code = HashAuthorizationCode(code),
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
    public async Task ExchangeAsync_Succeeds_When_Acr_DerivedFromAmr_EmitsAcrInIdToken()
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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

        var code = "code-acr-derived";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
            UserId = userId,
            ClientId = "c1",
            RedirectUri = "https://cb",
            ScopesJson = JsonSerializer.Serialize(new[] { "openid", "offline_access" }),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TenantId = tenantId,
            ClaimsJson = null
        });
        await db.SaveChangesAsync();

        // Simulate a local sign-in where only AMR is present.
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new Claim(OidcConstants.Claims.Amr, "pwd")
        ], "test"));

        var metadataSvc = new AuthorizationMetadataService(metaStore, db);
        await metadataSvc.PopulateMetadataAsync(http, code, CancellationToken.None);

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);

        Assert.IsNotNull(capturedIdTokenClaims);
        var acr = capturedIdTokenClaims!.FirstOrDefault(c => c.Type == OidcConstants.Claims.Acr)?.Value;
        Assert.AreEqual(OidcConstants.AcrValues.Password, acr);
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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
            Code = HashAuthorizationCode(code),
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
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
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
            Code = HashAuthorizationCode(code),
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

    [TestMethod]
    public async Task ExchangeAsync_Succeeds_When_EssentialMappedIdTokenClaim_Is_Present()
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
            .ReturnsAsync("jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
            refreshSvc.Object,
            revocationSvc.Object,
            Options(o => o.PropagateMappedClaimsToIdToken = ["employee_id"]),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-essential-mapped-id-claim";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        var claimsJson = "{\"id_token\":{\"employee_id\":{\"essential\":true}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
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
        metaStore.SetMappedClaims(code, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["employee_id"] = "E-123"
        });

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
        Assert.IsNotNull(capturedIdTokenClaims);
        Assert.IsTrue(capturedIdTokenClaims!.Any(c => c.Type == "employee_id" && c.Value == "E-123"));
    }

    [TestMethod]
    public async Task ExchangeAsync_Omits_NonEssentialMappedClaim_When_ValueConstraintMismatch()
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
            .ReturnsAsync("jwt");

        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc
            .Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<string?>()))
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
            CreateKeyProvider(),
            refreshSvc.Object,
            revocationSvc.Object,
            Options(o => o.PropagateMappedClaimsToIdToken = ["employee_id"]),
            metaStore,
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Options()),
            logger.Object);

        var code = "code-nonessential-mapped-id-claim";
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Realms.Add(new Realm { Id = Guid.NewGuid(), Name = "r1", TenantId = tenantId });
        await db.SaveChangesAsync();
        var realmId = db.Realms.First().Id;

        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", RealmId = realmId, TenantId = tenantId });
        db.Users.Add(new User { Id = userId, Username = "u1", TenantId = tenantId });

        var claimsJson = "{\"id_token\":{\"employee_id\":{\"essential\":false,\"value\":\"E-expected\"}}}";

        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Code = HashAuthorizationCode(code),
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
        metaStore.SetMappedClaims(code, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["employee_id"] = "E-actual"
        });

        var request = new AuthorizationCodeExchangeRequest(code, "https://cb", "c1", "verifier", "https://issuer");
        var (ok, _, error, status) = await exchanger.ExchangeAsync(request, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(200, status);
        Assert.IsNull(error);
        Assert.IsNotNull(capturedIdTokenClaims);
        Assert.IsFalse(capturedIdTokenClaims!.Any(c => c.Type == "employee_id"));
    }
}
