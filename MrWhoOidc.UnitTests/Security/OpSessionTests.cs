using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Infrastructure.Security;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// F1: the server-side OP session. Its id is the <c>sid</c> of the ID token, and the session decides whether the
/// cookie is still valid.
/// </summary>
[TestClass]
public sealed class OpSessionTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static (OpSessionService service, AuthDbContext db) CreateService(Action<OpSessionOptions>? configure = null)
    {
        var db = TestDataSeeder.CreateInMemoryDb();

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.CurrentTenant).Returns(new TenantContext { TenantId = TenantId, Slug = "default" });

        var settings = new Mock<ITenantSettingsService>();
        settings.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings());

        var options = new OpSessionOptions();
        configure?.Invoke(options);

        var service = new OpSessionService(
            db,
            tenantAccessor.Object,
            settings.Object,
            Options.Create(options),
            NullLogger<OpSessionService>.Instance);

        return (service, db);
    }

    [TestMethod]
    public async Task ASession_CarriesTheTimeouts_AndIsValid()
    {
        var (service, db) = CreateService(o =>
        {
            o.IdleTimeoutMinutes = 30;
            o.AbsoluteTimeoutHours = 8;
        });
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);

        Assert.AreEqual(TenantId, session.TenantId);
        Assert.AreEqual(UserId, session.UserId);
        Assert.IsNull(session.RevokedAt);
        Assert.IsTrue(session.IdleExpiresAt > DateTimeOffset.UtcNow.AddMinutes(29));
        Assert.IsTrue(session.ExpiresAt > DateTimeOffset.UtcNow.AddHours(7));
        Assert.AreEqual(OpSessionStatus.Valid, await service.CheckAsync(session.Id));
    }

    [TestMethod]
    public async Task TheTenantSettings_OverrideTheTimeouts()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        await using var _db = db;

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.CurrentTenant).Returns(new TenantContext { TenantId = TenantId, Slug = "default" });

        var settings = new Mock<ITenantSettingsService>();
        settings.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings
        {
            Auth = new AuthTenantSettings
            {
                OpSession = new OpSessionTenantSettings { IdleTimeoutMinutes = 5, AbsoluteTimeoutHours = 1 }
            }
        });

        var service = new OpSessionService(
            db,
            tenantAccessor.Object,
            settings.Object,
            Options.Create(new OpSessionOptions { IdleTimeoutMinutes = 30, AbsoluteTimeoutHours = 8 }),
            NullLogger<OpSessionService>.Instance);

        var session = await service.CreateAsync(TenantId, UserId);

        Assert.IsTrue(session.IdleExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.IsTrue(session.ExpiresAt <= DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1));
    }

    [TestMethod]
    public async Task ARevokedSession_IsNotValid()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);

        await service.RevokeAsync(session.Id, "the user signed out", revokeTokens: true);
        Assert.AreEqual(OpSessionStatus.Revoked, await service.CheckAsync(session.Id));
        Assert.AreEqual(0, await service.RevokeAsync(session.Id, "again", revokeTokens: true), "a revoked session is not revoked twice");
    }

    [TestMethod]
    public async Task AnExpiredSession_IsNotValid()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);
        session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.AreEqual(OpSessionStatus.Expired, await service.CheckAsync(session.Id));
    }

    [TestMethod]
    public async Task AnIdleSession_IsNotValid()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);
        session.IdleExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.AreEqual(OpSessionStatus.IdleExpired, await service.CheckAsync(session.Id));
    }

    [TestMethod]
    public async Task AnUnknownSession_IsNotFound()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        Assert.AreEqual(OpSessionStatus.NotFound, await service.CheckAsync(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task TheClientsOfTheSession_AreRecorded()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);
        await service.RegisterClientAsync(session.Id, "client-a");
        await service.RegisterClientAsync(session.Id, "client-b");
        await service.RegisterClientAsync(session.Id, "client-a");

        var clients = await service.GetClientIdsAsync(session.Id);
        CollectionAssert.AreEquivalent(new[] { "client-a", "client-b" }, clients.ToList());
    }

    [TestMethod]
    public async Task TheCookieValidator_GivesACookieWithoutASessionOne_AndKeepsIt()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var context = CreateValidationContext(db, service, claims: new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, UserId.ToString())
        });

        await OpSessionCookieValidator.ValidateAsync(context);

        Assert.IsFalse(context.Principal is null, "the cookie must stay valid");
        var sid = context.Principal!.FindFirst(OidcConstants.Claims.Sid)?.Value;
        Assert.IsNotNull(sid, "the cookie carries the session id");
        Assert.AreEqual(1, await db.OpSessions.CountAsync());
        Assert.AreEqual(OpSessionStatus.Valid, await service.CheckAsync(Guid.Parse(sid!)));
    }

    [TestMethod]
    public async Task TheCookieValidator_RejectsACookieOfARevokedSession()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);
        await service.RevokeAsync(session.Id, "the user signed out elsewhere", revokeTokens: true);

        var context = CreateValidationContext(db, service, claims: new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, UserId.ToString()),
            new System.Security.Claims.Claim(OidcConstants.Claims.Sid, session.Id.ToString("N"))
        });

        await OpSessionCookieValidator.ValidateAsync(context);

        Assert.IsNull(context.Principal, "a revoked session must sign the user out");
    }

    [TestMethod]
    public async Task TheCookieValidator_RejectsACookieWithAMalformedSessionId()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var context = CreateValidationContext(db, service, claims: new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, UserId.ToString()),
            new System.Security.Claims.Claim(OidcConstants.Claims.Sid, "not-a-session-id")
        });

        await OpSessionCookieValidator.ValidateAsync(context);

        Assert.IsNull(context.Principal, "a cookie with a malformed session id was not issued by this OP");
        Assert.AreEqual(0, await db.OpSessions.CountAsync());
    }

    [TestMethod]
    public async Task TheSessionsOfAUser_AreListed()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var other = Guid.NewGuid();
        await service.CreateAsync(TenantId, UserId);
        await service.CreateAsync(TenantId, UserId);
        await service.CreateAsync(TenantId, other);

        var sessions = await service.ListForUserAsync(TenantId, UserId);

        Assert.AreEqual(2, sessions.Count, "only the sessions of the user, in this tenant");
    }

    [TestMethod]
    public async Task RevokingASession_RevokesTheRefreshTokensItProduced()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);
        var token = new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = "hash-1",
            TenantId = TenantId,
            UserId = UserId,
            ClientId = "c1",
            SessionId = session.Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        var otherToken = new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = "hash-2",
            TenantId = TenantId,
            UserId = UserId,
            ClientId = "c1",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Tokens.AddRange(token, otherToken);
        await db.SaveChangesAsync();

        var revoked = await service.RevokeAsync(session.Id, "the user signed out", revokeTokens: true);

        Assert.AreEqual(1, revoked, "the refresh token of the session is revoked");
        Assert.IsNotNull((await db.Tokens.SingleAsync(t => t.TokenHash == "hash-1")).RevokedAt);
        Assert.IsNull((await db.Tokens.SingleAsync(t => t.TokenHash == "hash-2")).RevokedAt,
            "a token without a session is not affected");
    }

    [TestMethod]
    public async Task RevokingAllTheOtherSessions_KeepsTheCurrentOne()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var current = await service.CreateAsync(TenantId, UserId);
        var other = await service.CreateAsync(TenantId, UserId);
        var foreign = await service.CreateAsync(TenantId, Guid.NewGuid());

        var revoked = await service.RevokeAllForUserAsync(TenantId, UserId, current.Id, "the user ended the other sessions");

        Assert.AreEqual(1, revoked);
        Assert.IsNull((await db.OpSessions.SingleAsync(s => s.Id == current.Id)).RevokedAt, "the current session stays");
        Assert.IsNotNull((await db.OpSessions.SingleAsync(s => s.Id == other.Id)).RevokedAt);
        Assert.IsNull((await db.OpSessions.SingleAsync(s => s.Id == foreign.Id)).RevokedAt, "another user's session is not touched");
    }

    [TestMethod]
    public async Task ACredentialChange_RevokesTheSessionsOfTheAccount()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var accountId = Guid.NewGuid();
        var user = new User { TenantId = TenantId, Username = "alice", UserAccountId = accountId };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var session = await service.CreateAsync(TenantId, user.Id, userAccountId: accountId);

        var revoked = await service.RevokeForAccountAsync(accountId, "the credentials changed");

        Assert.AreEqual(1, revoked);
        Assert.AreEqual(OpSessionStatus.Revoked, await service.CheckAsync(session.Id));
    }

    [TestMethod]
    public async Task TheStatusCheck_DoesNotMoveTheIdleDeadline()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);
        var before = session.IdleExpiresAt;

        await service.GetStatusAsync(session.Id);

        var stored = await db.OpSessions.SingleAsync(s => s.Id == session.Id);
        Assert.AreEqual(before, stored.IdleExpiresAt, "a poll must not keep an idle session alive");
    }

    [TestMethod]
    public async Task TheCookieValidator_KeepsACookieOfAValidSession()
    {
        var (service, db) = CreateService();
        await using var _db = db;

        var session = await service.CreateAsync(TenantId, UserId);

        var context = CreateValidationContext(db, service, claims: new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, UserId.ToString()),
            new System.Security.Claims.Claim(OidcConstants.Claims.Sid, session.Id.ToString("N"))
        });

        await OpSessionCookieValidator.ValidateAsync(context);

        Assert.IsNotNull(context.Principal);
        Assert.AreEqual(1, await db.OpSessions.CountAsync(), "a valid session is not created twice");
    }

    private static CookieValidatePrincipalContext CreateValidationContext(
        AuthDbContext db,
        IOpSessionService sessionService,
        System.Security.Claims.Claim[] claims)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(sessionService);
        services.AddSingleton(Options.Create(new OpSessionOptions { Enabled = true }));
        services.AddDataProtection();
        services.AddLogging();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.CurrentTenant).Returns(new TenantContext { TenantId = TenantId, Slug = "default" });
        services.AddSingleton(tenantAccessor.Object);
        var provider = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("idp.example");

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Cookies");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            null,
            typeof(CookieAuthenticationHandler));

        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), scheme.Name);
        var options = new CookieAuthenticationOptions();

        return new CookieValidatePrincipalContext(http, scheme, options, ticket);
    }
}
