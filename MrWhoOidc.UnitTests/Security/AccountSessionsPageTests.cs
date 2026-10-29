using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Pages.Account;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// F1: the session page lists the real OP sessions of the signed-in user (and only those), and it can end them.
/// </summary>
[TestClass]
public sealed class AccountSessionsPageTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [TestMethod]
    public async Task ThePage_ListsTheSessionsOfTheSignedInUser_Only()
    {
        var (model, db, user, ownSession) = await CreateModelAsync();
        await using var _db = db;

        await model.OnGetAsync();

        Assert.AreEqual(1, model.Sessions.Count);
        Assert.AreEqual(ownSession.Id, model.Sessions[0].Id);
        Assert.IsTrue(model.Sessions[0].IsCurrent, "the session of the cookie is the current one");
        CollectionAssert.Contains(model.Sessions[0].ClientIds.ToList(), "spa", "the applications that took part are listed");
    }

    [TestMethod]
    public async Task ThePage_DoesNotShowTheSessionsOfAnotherUser()
    {
        var (model, db, _, _) = await CreateModelAsync();
        await using var _db = db;

        // A session of another user in the same tenant.
        db.OpSessions.Add(new OpSession
        {
            TenantId = TenantId,
            UserId = Guid.NewGuid(),
            AuthTime = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            IdleExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        });
        await db.SaveChangesAsync();

        await model.OnGetAsync();

        Assert.AreEqual(1, model.Sessions.Count);
    }

    [TestMethod]
    public async Task RevokingASession_EndsIt_AndItsRefreshTokens()
    {
        var (model, db, _, ownSession) = await CreateModelAsync();
        await using var _db = db;

        // A second session of the same user (another browser).
        var other = new OpSession
        {
            TenantId = TenantId,
            UserId = ownSession.UserId,
            AuthTime = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            IdleExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        db.OpSessions.Add(other);
        db.Tokens.Add(new Token
        {
            Type = "refresh",
            TokenHash = "hash-" + Guid.NewGuid().ToString("N"),
            TenantId = TenantId,
            UserId = ownSession.UserId,
            ClientId = "spa",
            SessionId = other.Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();

        var result = await model.OnPostRevokeAsync(other.Id);

        Assert.IsInstanceOfType<RedirectToPageResult>(result);
        var stored = await db.OpSessions.SingleAsync(s => s.Id == other.Id);
        Assert.IsNotNull(stored.RevokedAt, "the session is ended");

        var token = await db.Tokens.SingleAsync(t => t.SessionId == other.Id);
        Assert.IsNotNull(token.RevokedAt, "the refresh tokens of the session end with it");

        Assert.IsNull((await db.OpSessions.SingleAsync(s => s.Id == ownSession.Id)).RevokedAt,
            "the current session is not touched");
    }

    [TestMethod]
    public async Task TheCurrentSession_CannotBeEndedFromThePage()
    {
        var (model, db, _, ownSession) = await CreateModelAsync();
        await using var _db = db;

        var result = await model.OnPostRevokeAsync(ownSession.Id);

        Assert.IsInstanceOfType<RedirectToPageResult>(result);
        var stored = await db.OpSessions.SingleAsync(s => s.Id == ownSession.Id);
        Assert.IsNull(stored.RevokedAt, "the current session stays; the page is the current session");
        Assert.IsNotNull(model.Message);
    }

    [TestMethod]
    public async Task RevokingAll_EndsTheOtherSessions_AndKeepsTheCurrentOne()
    {
        var (model, db, _, ownSession) = await CreateModelAsync();
        await using var _db = db;

        var other = new OpSession
        {
            TenantId = TenantId,
            UserId = ownSession.UserId,
            AuthTime = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            IdleExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        db.OpSessions.Add(other);
        await db.SaveChangesAsync();

        await model.OnPostRevokeAllAsync();

        Assert.IsNull((await db.OpSessions.SingleAsync(s => s.Id == ownSession.Id)).RevokedAt);
        Assert.IsNotNull((await db.OpSessions.SingleAsync(s => s.Id == other.Id)).RevokedAt);
    }

    [TestMethod]
    public async Task TheStoreIsOff_ThePage_ExplainsIt()
    {
        var (model, db, _, _) = await CreateModelAsync(enabled: false);
        await using var _db = db;

        await model.OnGetAsync();

        Assert.IsTrue(model.StoreDisabled);
        Assert.AreEqual(0, model.Sessions.Count);
        Assert.IsNotNull(model.Message);
    }

    private static async Task<(SessionsModel Model, AuthDbContext Db, User User, OpSession Session)> CreateModelAsync(bool enabled = true)
    {
        var db = TestDataSeeder.CreateInMemoryDb();

        var user = new User { TenantId = TenantId, Username = "alice", Email = "alice@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var session = new OpSession
        {
            TenantId = TenantId,
            UserId = user.Id,
            AuthTime = now,
            LastSeenAt = now,
            ExpiresAt = now.AddHours(8),
            IdleExpiresAt = now.AddMinutes(30),
            UserAgent = "TestBrowser/1.0"
        };
        db.OpSessions.Add(session);
        db.OpSessionClients.Add(new OpSessionClient
        {
            TenantId = TenantId,
            SessionId = session.Id,
            ClientId = "spa",
            FirstSeenAt = now
        });
        db.Tokens.Add(new Token
        {
            Type = "refresh",
            TokenHash = "hash-" + Guid.NewGuid().ToString("N"),
            TenantId = TenantId,
            UserId = user.Id,
            ClientId = "spa",
            SessionId = session.Id,
            ExpiresAt = now.AddDays(1)
        });
        await db.SaveChangesAsync();

        var settings = new Mock<ITenantSettingsService>();
        settings.Setup(s => s.GetCurrentTenantSettingsAsync()).ReturnsAsync(new TenantSettings());

        var sessions = new OpSessionService(
            db,
            MockTenantAccessor.CreateWithTenant(TenantId, "acme"),
            settings.Object,
            Options.Create(new OpSessionOptions { Enabled = enabled }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpSessionService>.Instance);

        var model = new SessionsModel(
            db,
            new UserAgentParser(),
            sessions,
            MockTenantAccessor.CreateWithTenant(TenantId, "acme"),
            Options.Create(new OpSessionOptions { Enabled = enabled }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionsModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                            new Claim(OidcConstants.Claims.Sid, session.Id.ToString("N"))
                        },
                        "Cookies"))
                }
            }
        };

        return (model, db, user, session);
    }
}
