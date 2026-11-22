using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Impersonation;
using MrWhoOidc.WebAuth.Middleware;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class UserImpersonationTests
{
    [TestMethod]
    public void UserImpersonation_IsDisabledByDefault()
    {
        Assert.IsFalse(new AuthOptions().EnableUserImpersonation);
    }

    [TestMethod]
    [DataRow("/platform-admin/user-impersonation")]
    [DataRow("/platform-admin/user-impersonation/history")]
    [DataRow("/impersonation/stop")]
    [DataRow("/login")]
    [DataRow("/logout")]
    [DataRow("/t/default/login")]
    [DataRow("/t/default/account/logout")]
    [DataRow("/t/default/mfa")]
    public void ControlPaths_AreNeverOverlaid(string path)
    {
        Assert.IsTrue(UserImpersonationMiddleware.IsControlPath(new PathString(path)), path);
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/t/default/account")]
    [DataRow("/t/default/admin/clients")]
    [DataRow("/t/default/authorize")]
    [DataRow("/t/default/userinfo")]
    public void BusinessPaths_AreEligibleForOverlay(string path)
    {
        Assert.IsFalse(UserImpersonationMiddleware.IsControlPath(new PathString(path)), path);
    }

    [TestMethod]
    public async Task Store_GetById_IgnoresTenantScope()
    {
        var tenantId = Guid.NewGuid();
        await using var db = CreateDb();
        var store = new UserImpersonationStore(db);
        var session = CreateSession(tenantId);
        await store.CreateAsync(session);

        var loaded = await store.GetByIdAsync(session.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(tenantId, loaded.TenantId);
        Assert.IsNull(await store.GetByIdAsync(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task Store_GetActiveForActor_ExcludesExpiredSessions()
    {
        await using var db = CreateDb();
        var store = new UserImpersonationStore(db);
        var actorAccountId = Guid.NewGuid();
        var expired = CreateSession(Guid.NewGuid());
        expired.ActorUserAccountId = actorAccountId;
        expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var active = CreateSession(Guid.NewGuid());
        active.ActorUserAccountId = actorAccountId;

        await store.CreateAsync(expired);
        await store.CreateAsync(active);

        var found = await store.GetActiveForActorAsync(actorAccountId);

        Assert.IsNotNull(found);
        Assert.AreEqual(active.Id, found.Id);
    }

    [TestMethod]
    public async Task Store_End_RevokesTargetSessionAndOnlyItsTokens()
    {
        await using var db = CreateDb();
        var store = new UserImpersonationStore(db);
        var tenantId = Guid.NewGuid();
        var targetOpSessionId = Guid.NewGuid();
        var session = CreateSession(tenantId);
        session.TargetOpSessionId = targetOpSessionId;

        var targetSession = new OpSession
        {
            Id = targetOpSessionId,
            TenantId = tenantId,
            UserId = session.TargetUserId,
            AuthTime = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IdleExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var targetToken = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = "target-token",
            UserId = session.TargetUserId,
            ClientId = "blazor-web",
            SessionId = targetOpSessionId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var otherToken = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = "other-token",
            UserId = session.TargetUserId,
            ClientId = "blazor-web",
            SessionId = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.OpSessions.Add(targetSession);
        db.Tokens.AddRange(targetToken, otherToken);
        db.UserImpersonationSessions.Add(session);
        await db.SaveChangesAsync();

        var endedAt = DateTimeOffset.UtcNow;
        var ended = await store.EndAsync(
            session.Id, UserImpersonationStatus.Ended, endedAt, null, null);

        Assert.IsTrue(ended);
        var persisted = await db.UserImpersonationSessions.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == session.Id);
        Assert.AreEqual(UserImpersonationStatus.Ended, persisted.Status);
        Assert.AreEqual(endedAt, persisted.EndedAt);

        var persistedTarget = await db.OpSessions.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == targetOpSessionId);
        Assert.IsNotNull(persistedTarget.RevokedAt);

        var persistedTargetToken = await db.Tokens.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == targetToken.Id);
        Assert.IsNotNull(persistedTargetToken.RevokedAt);

        var persistedOtherToken = await db.Tokens.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == otherToken.Id);
        Assert.IsNull(persistedOtherToken.RevokedAt);
    }

    [TestMethod]
    public async Task ActorBoundToken_ValidatesForActor_NotForTarget_AndLeavesTargetTokenIntact()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAntiforgery()
            .BuildServiceProvider();
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var options = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        var actor = Principal(Guid.NewGuid());
        var target = Principal(Guid.NewGuid());

        var bootstrap = new DefaultHttpContext { RequestServices = services, User = target };
        bootstrap.Request.Scheme = "https";
        bootstrap.Request.Host = new HostString("idp.example");
        var cookieToken = antiforgery.GetAndStoreTokens(bootstrap).CookieToken!;
        var cookie = $"{options.Cookie.Name}={cookieToken}";

        // The first call in an overlaid request mints a target-bound token (a sidebar form does this
        // before the impersonation banner is rendered).
        var page = new DefaultHttpContext { RequestServices = services, User = target };
        page.Request.Scheme = "https";
        page.Request.Host = new HostString("idp.example");
        page.Request.Headers.Cookie = cookie;
        var targetToken = antiforgery.GetAndStoreTokens(page).RequestToken!;
        page.Items[UserImpersonationMiddleware.ActorPrincipalItemKey] = actor;

        var actorToken = UserImpersonationAntiforgery.GetActorBoundRequestToken(page, antiforgery);

        Assert.AreNotEqual(targetToken, actorToken);
        Assert.IsTrue(await IsTokenValidAsync(services, actorToken, actor, cookie),
            "The actor-bound token must validate on a control request processed as the actor.");
        Assert.IsFalse(await IsTokenValidAsync(services, actorToken, target, cookie),
            "The actor-bound token must not validate as the target.");
        Assert.IsFalse(await IsTokenValidAsync(services, targetToken, actor, cookie),
            "The target-bound token must not validate on a control request processed as the actor.");
        Assert.IsTrue(await IsTokenValidAsync(services, targetToken, target, cookie),
            "Minting the actor token must not disturb the target token cached on the page request.");
    }

    private static ClaimsPrincipal Principal(Guid id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "Test"));

    private static async Task<bool> IsTokenValidAsync(
        IServiceProvider services,
        string token,
        ClaimsPrincipal user,
        string cookie)
    {
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var options = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        var request = new DefaultHttpContext { RequestServices = services, User = user };
        request.Request.Method = "POST";
        request.Request.Scheme = "https";
        request.Request.Host = new HostString("idp.example");
        request.Request.Headers.Cookie = cookie;
        request.Request.Headers[options.HeaderName] = token;
        return await antiforgery.IsRequestValidAsync(request);
    }

    private static AuthDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AuthDbContext(options);
    }

    private static UserImpersonationSession CreateSession(Guid tenantId) => new()
    {
        ActorUserAccountId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        ActorUsername = "admin",
        TenantId = tenantId,
        TenantName = "Tenant",
        TenantSlug = "tenant",
        TargetUserId = Guid.NewGuid(),
        TargetUserAccountId = Guid.NewGuid(),
        TargetUsername = "alice",
        TargetOpSessionId = Guid.NewGuid(),
        Status = UserImpersonationStatus.Active,
        Reason = "Troubleshooting",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
    };
}
