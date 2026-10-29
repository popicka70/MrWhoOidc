using System;
using System.Collections.Generic;
using System.Linq;
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
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Services.Token;

/// <summary>
/// F1: the refresh tokens are bound to the OP session that produced them. Ending the session ends the tokens:
/// the user stays signed out at the relying party.
/// </summary>
[TestClass]
public sealed class RefreshTokenSessionBindingTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task ARevokedSession_RevokesTheRefreshToken_TheRefreshGrantFails()
    {
        using var db = CreateDb();
        var user = new User { Username = "u" };
        db.Users.Add(user);
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", TenantId = TenantId });
        await db.SaveChangesAsync();

        var session = new OpSession
        {
            TenantId = TenantId,
            UserId = user.Id,
            AuthTime = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            IdleExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        db.OpSessions.Add(session);

        var rt = new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = CryptoHelper.ComputeSha256Base64("rt"),
            ClientId = "c1",
            UserId = user.Id,
            ScopesJson = "[\"openid\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            SessionId = session.Id
        };
        db.Tokens.Add(rt);
        await db.SaveChangesAsync();

        // The session ends: the refresh token of the session ends with it.
        var sessions = new OpSessionService(
            db,
            MockTenantAccessor.CreateWithTenant(TenantId, "acme"),
            new MockTenantSettingsService(),
            Microsoft.Extensions.Options.Options.Create(new OpSessionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpSessionService>.Instance);
        await sessions.RevokeAsync(session.Id, "the user signed out", revokeTokens: true);

        Assert.IsNotNull((await db.Tokens.SingleAsync(t => t.TokenHash == rt.TokenHash)).RevokedAt,
            "the refresh token of the session is revoked with it");

        var (ok, _, error, status) = await CreateExchanger(db).ExchangeAsync(
            new RefreshTokenExchangeRequest("rt", "c1", "https://issuer"), CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task TheRotation_KeepsTheSessionOfTheToken()
    {
        using var db = CreateDb();
        var user = new User { Username = "u" };
        db.Users.Add(user);
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", TenantId = TenantId });
        await db.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var rt = new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = CryptoHelper.ComputeSha256Base64("rt"),
            ClientId = "c1",
            UserId = user.Id,
            ScopesJson = "[\"openid\"]",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            SessionId = sessionId
        };
        db.Tokens.Add(rt);
        await db.SaveChangesAsync();

        Guid? rotatedSessionId = null;
        var refreshSvc = new Mock<IRefreshTokenService>();
        refreshSvc.Setup(x => x.CreateRefreshTokenAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<Guid?>()))
            .Callback((Guid _, string _, string[] _, string? _, string? _, CancellationToken _, DateTimeOffset? _, string? _, string? _, string? _, Guid? session) => rotatedSessionId = session)
            .ReturnsAsync(("new-rt", "new-hash"));

        var exchanger = CreateExchanger(db, refreshSvc.Object);
        var (ok, _, _, _) = await exchanger.ExchangeAsync(
            new RefreshTokenExchangeRequest("rt", "c1", "https://issuer"), CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(sessionId, rotatedSessionId, "the rotated token stays in the session of the grant");
    }

    [TestMethod]
    public async Task ARefreshTokenWithoutASession_IsNotAffectedByTheRevocationOfAnotherSession()
    {
        using var db = CreateDb();
        var user = new User { Username = "u" };
        db.Users.Add(user);
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", TenantId = TenantId });
        await db.SaveChangesAsync();

        var session = new OpSession
        {
            TenantId = TenantId,
            UserId = user.Id,
            AuthTime = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            IdleExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        db.OpSessions.Add(session);

        // The client-credentials grant has no session: its token is not bound to one.
        var rt = new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = CryptoHelper.ComputeSha256Base64("rt"),
            ClientId = "c1",
            UserId = user.Id,
            ScopesJson = "[\"openid\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Tokens.Add(rt);
        await db.SaveChangesAsync();

        var sessions = new OpSessionService(
            db,
            MockTenantAccessor.CreateWithTenant(TenantId, "acme"),
            new MockTenantSettingsService(),
            Microsoft.Extensions.Options.Options.Create(new OpSessionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpSessionService>.Instance);
        await sessions.RevokeAsync(session.Id, "the user signed out", revokeTokens: true);

        var (ok, _, error, _) = await CreateExchanger(db).ExchangeAsync(
            new RefreshTokenExchangeRequest("rt", "c1", "https://issuer"), CancellationToken.None);

        Assert.IsTrue(ok, "a token without a session keeps working");
        Assert.IsNull(error);
    }

    [TestMethod]
    public async Task ARefreshTokenOfAnEndedSession_IsRefused()
    {
        using var db = CreateDb();
        var user = new User { Username = "u" };
        db.Users.Add(user);
        db.Clients.Add(new MrWhoOidc.Auth.Persistence.Client { ClientId = "c1", TenantId = TenantId });
        await db.SaveChangesAsync();

        // The session was swept: the row is gone, but the token still points at it.
        var sessionId = Guid.NewGuid();
        var rt = new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = CryptoHelper.ComputeSha256Base64("rt"),
            ClientId = "c1",
            UserId = user.Id,
            ScopesJson = "[\"openid\"]",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            SessionId = sessionId
        };
        db.Tokens.Add(rt);
        await db.SaveChangesAsync();

        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.CheckAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpSessionStatus.NotFound);

        var (ok, _, error, status) = await CreateExchanger(db, opSessions: sessions.Object).ExchangeAsync(
            new RefreshTokenExchangeRequest("rt", "c1", "https://issuer"), CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_grant", error);
    }

    private static RefreshTokenExchanger CreateExchanger(
        AuthDbContext db,
        IRefreshTokenService? refreshService = null,
        IOpSessionService? opSessions = null)
    {
        var jwtSvc = new Mock<IJwtService>();
        jwtSvc.Setup(x => x.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<System.Security.Claims.Claim>>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("jwt-at");

        IRefreshTokenService refreshSvc = refreshService ?? new Mock<IRefreshTokenService>
        {
            DefaultValue = DefaultValue.Empty
        }.Object;
        if (refreshService is null)
        {
            var fallback = new Mock<IRefreshTokenService>();
            fallback.Setup(x => x.CreateRefreshTokenAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<Guid?>()))
                .ReturnsAsync(("rotated-rt", CryptoHelper.ComputeSha256Base64("rotated-rt")));
            refreshSvc = fallback.Object;
        }
        var revocationSvc = Mock.Of<IRevocationService>();
        var settingsSvc = new MockTenantSettingsService();
        var scopeResolver = new MockScopeResolver();
        var entitlementsProvider = new NoopEntitlementsProvider();
        var tenantsClaimService = new NoopTenantsClaimService();
        var pairwiseSubjectService = new Mock<IPairwiseSubjectService>();
        pairwiseSubjectService
            .Setup(x => x.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken __) => userId.ToString());
        var claimBuilder = new Mock<IAccessTokenClaimBuilder>();
        var logger = new Mock<ILogger<RefreshTokenExchanger>>();

        return new RefreshTokenExchanger(
            db,
            jwtSvc.Object,
            refreshSvc,
            revocationSvc,
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            settingsSvc,
            entitlementsProvider,
            tenantsClaimService,
            pairwiseSubjectService.Object,
            claimBuilder.Object,
            new TokenLifetimeResolver(),
            new OpaqueTokenPolicy(Microsoft.Extensions.Options.Options.Create(new AuthOptions())),
            opSessions);
    }
}
