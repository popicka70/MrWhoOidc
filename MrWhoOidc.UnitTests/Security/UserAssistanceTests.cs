using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Security;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Assistance;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Middleware;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class UserAssistanceTests
{
    [TestMethod]
    public void UserAssistance_IsDisabledByDefault()
    {
        Assert.IsFalse(new AuthOptions().EnableUserAssistance);
    }

    [TestMethod]
    [DataRow("/account/assistance")]
    [DataRow("/account/assistance/accept")]
    [DataRow("/account/assistance/stop")]
    [DataRow("/t/default/account/assistance")]
    [DataRow("/admin/assistance")]
    public void AssistancePaths_AreControlPaths(string path)
    {
        Assert.IsTrue(UserImpersonationMiddleware.IsControlPath(new PathString(path)), path);
    }

    [TestMethod]
    public void Settings_DefaultsAreSafeAndExplicit()
    {
        var settings = AssistanceSettingsResolver.Resolve(null);

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(UserAssistanceRightsMode.Intersection, settings.RightsMode);
        CollectionAssert.AreEqual(new[] { "tenant-admin" }, settings.ReadOnlyHelperRoles.ToArray());
        CollectionAssert.AreEqual(new[] { "tenant-admin" }, settings.ManagerRoles.ToArray());
        Assert.AreEqual(0, settings.ReadWriteHelperRoles.Count);
        Assert.AreEqual(0, settings.RequesterRoles.Count);
        Assert.AreEqual(AssistanceReadOnlyTokenPolicy.MarkerOnly, settings.ReadOnlyTokenPolicy);
        Assert.AreEqual(15, settings.DefaultDurationMinutes);
        Assert.AreEqual(60, settings.MaxDurationMinutes);
        Assert.AreEqual(10, settings.RequestLifetimeMinutes);
    }

    [TestMethod]
    public async Task RoleResolver_IntersectionDoesNotGrantRequesterOnlyRoles()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var realm = new Realm { TenantId = tenantId, Name = "default" };
        var shared = new Role { TenantId = tenantId, RealmId = realm.Id, Name = "shared" };
        var requesterOnly = new Role { TenantId = tenantId, RealmId = realm.Id, Name = "requester-only" };
        var helperOnly = new Role { TenantId = tenantId, RealmId = realm.Id, Name = "helper-only" };
        var requesterId = Guid.NewGuid();
        var helperId = Guid.NewGuid();
        db.Realms.Add(realm);
        db.Roles.AddRange(shared, requesterOnly, helperOnly);
        db.UserRealmRoleAssignments.AddRange(
            new UserRealmRoleAssignment { UserId = requesterId, RoleId = shared.Id, RealmId = realm.Id, IsActive = true },
            new UserRealmRoleAssignment { UserId = requesterId, RoleId = requesterOnly.Id, RealmId = realm.Id, IsActive = true },
            new UserRealmRoleAssignment { UserId = helperId, RoleId = shared.Id, RealmId = realm.Id, IsActive = true },
            new UserRealmRoleAssignment { UserId = helperId, RoleId = helperOnly.Id, RealmId = realm.Id, IsActive = true });
        await db.SaveChangesAsync();

        var resolver = new UserAssistanceRoleResolver(db);
        var session = new UserAssistanceSession
        {
            TenantId = tenantId,
            RequesterUserId = requesterId,
            HelperUserId = helperId,
            RightsMode = UserAssistanceRightsMode.Intersection
        };

        var intersection = await resolver.GetEffectiveRoleIdsAsync(session);
        Assert.AreEqual(1, intersection.Count);
        Assert.IsTrue(intersection.Contains(shared.Id));

        session.RightsMode = UserAssistanceRightsMode.FullTarget;
        var full = await resolver.GetEffectiveRoleIdsAsync(session);
        Assert.AreEqual(2, full.Count);
        Assert.IsTrue(full.Contains(shared.Id));
        Assert.IsTrue(full.Contains(requesterOnly.Id));
        Assert.IsFalse(full.Contains(helperOnly.Id));
    }

    [TestMethod]
    public async Task Store_EndClearsInvitationAndRevokesOnlyTargetSessionTokens()
    {
        await using var db = CreateDb();
        var store = new UserAssistanceStore(db);
        var tenantId = Guid.NewGuid();
        var targetSessionId = Guid.NewGuid();
        var session = new UserAssistanceSession
        {
            TenantId = tenantId,
            RequesterUserId = Guid.NewGuid(),
            RequesterUserAccountId = Guid.NewGuid(),
            RequesterUsername = "alice",
            HelperUserId = Guid.NewGuid(),
            HelperUserAccountId = Guid.NewGuid(),
            HelperUsername = "bob",
            Status = UserAssistanceStatus.Active,
            TargetOpSessionId = targetSessionId,
            InvitationTokenHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            SessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        };
        var targetSession = new OpSession
        {
            Id = targetSessionId,
            TenantId = tenantId,
            UserId = session.RequesterUserId,
            AuthTime = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IdleExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var targetToken = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = "target",
            UserId = session.RequesterUserId,
            ClientId = "client",
            SessionId = targetSessionId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var otherToken = new Token
        {
            TenantId = tenantId,
            Type = "refresh",
            TokenHash = "other",
            UserId = session.RequesterUserId,
            ClientId = "client",
            SessionId = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.UserAssistanceSessions.Add(session);
        db.OpSessions.Add(targetSession);
        db.Tokens.AddRange(targetToken, otherToken);
        await db.SaveChangesAsync();

        var ended = await store.EndAsync(
            session.Id,
            UserAssistanceStatus.Ended,
            DateTimeOffset.UtcNow,
            session.RequesterUserAccountId,
            "ended");

        Assert.IsTrue(ended);
        var persisted = await db.UserAssistanceSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == session.Id);
        Assert.AreEqual(UserAssistanceStatus.Ended, persisted.Status);
        Assert.IsNull(persisted.InvitationTokenHash);
        Assert.IsNotNull((await db.OpSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == targetSessionId)).RevokedAt);
        Assert.IsNotNull((await db.Tokens.IgnoreQueryFilters().SingleAsync(t => t.Id == targetToken.Id)).RevokedAt);
        Assert.IsNull((await db.Tokens.IgnoreQueryFilters().SingleAsync(t => t.Id == otherToken.Id)).RevokedAt);
    }

    [TestMethod]
    public async Task RequestAcceptOverlayAndRequesterEnd_WorkEndToEnd()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Tenant", Slug = "tenant", Status = TenantStatus.Active };
        var realm = new Realm { TenantId = tenant.Id, Name = "default" };
        var helperRole = new Role { TenantId = tenant.Id, RealmId = realm.Id, Name = "tenant-admin" };
        var alice = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Username = "alice",
            Name = "Alice",
            Email = "alice@example.com",
            Status = UserStatus.Active
        };
        var bob = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Username = "bob",
            Name = "Bob",
            Email = "bob@example.com",
            Status = UserStatus.Active
        };
        var aliceAccount = new UserAccount { Id = Guid.NewGuid(), Username = "alice", Email = alice.Email, SecurityStamp = "alice-stamp" };
        var bobAccount = new UserAccount { Id = Guid.NewGuid(), Username = "bob", Email = bob.Email, SecurityStamp = "bob-stamp" };
        alice.UserAccountId = aliceAccount.Id;
        bob.UserAccountId = bobAccount.Id;
        db.Tenants.Add(tenant);
        db.Realms.Add(realm);
        db.Roles.Add(helperRole);
        db.Users.AddRange(alice, bob);
        db.UserAccounts.AddRange(aliceAccount, bobAccount);
        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
        {
            UserId = bob.Id,
            RoleId = helperRole.Id,
            RealmId = realm.Id,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var tenantAccessor = new TenantAccessor();
        tenantAccessor.SetTenant(new TenantContext
        {
            TenantId = tenant.Id,
            Slug = tenant.Slug,
            Name = tenant.Name,
            IssuerUri = "https://idp.example/t/tenant"
        });

        var settingsService = new Mock<ITenantSettingsService>();
        settingsService.Setup(s => s.GetTenantSettingsAsync(tenant.Id)).ReturnsAsync(new TenantSettings
        {
            Assistance = new AssistanceTenantSettings
            {
                Enabled = true,
                RightsMode = "intersection",
                ReadOnlyHelperRoles = ["tenant-admin"],
                ManagerRoles = ["tenant-admin"],
                DefaultDurationMinutes = 15,
                MaxDurationMinutes = 60,
                RequestLifetimeMinutes = 10
            }
        });

        var targetOpSessionId = Guid.NewGuid();
        var opSessions = new Mock<IOpSessionService>();
        opSessions.Setup(s => s.CreateAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpSession
            {
                Id = targetOpSessionId,
                TenantId = tenant.Id,
                UserId = alice.Id,
                AuthTime = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                IdleExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
        opSessions.Setup(s => s.CheckGlobalAsync(targetOpSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpSessionStatus.Valid);

        var emailSender = new Mock<IEmailSender>();
        var service = new UserAssistanceService(
            db,
            new UserAssistanceStore(db),
            new UserAssistanceRoleResolver(db),
            settingsService.Object,
            opSessions.Object,
            new UserAccountService(db),
            emailSender.Object,
            Options.Create(new AuthOptions { EnableUserAssistance = true }),
            Options.Create(new OidcOptions()),
            new MultiTenancyOptions { Enabled = false },
            tenantAccessor,
            new NoopAuditSink(),
            new UserAssistanceMetrics(),
            NullLogger<UserAssistanceService>.Instance);

        var alicePrincipal = Principal(alice.Id, aliceAccount.Id);
        var bobPrincipal = Principal(bob.Id, bobAccount.Id);
        var context = CreateContext();

        var created = await service.CreateRequestAsync(
            context,
            alicePrincipal,
            bob.Id,
            UserAssistanceMode.ReadOnly,
            "Please inspect my profile",
            30,
            "TICKET-1");

        Assert.IsTrue(created.Success, created.Error);
        Assert.IsNotNull(created.RequestId);
        var request = await db.UserAssistanceSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == created.RequestId);
        Assert.AreEqual(UserAssistanceStatus.Pending, request.Status);
        Assert.AreEqual(30, request.RequestedDurationMinutes);
        Assert.IsFalse(string.IsNullOrEmpty(request.InvitationTokenHash));

        var wrongHelper = Principal(Guid.NewGuid(), Guid.NewGuid());
        var rejected = await service.AcceptAsync(context, wrongHelper, request.Id, null);
        Assert.IsFalse(rejected.Success);

        var accepted = await service.AcceptAsync(context, bobPrincipal, request.Id, null);
        Assert.IsTrue(accepted.Success, accepted.Error);

        var active = await db.UserAssistanceSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == request.Id);
        Assert.AreEqual(UserAssistanceStatus.Active, active.Status);
        Assert.AreEqual(targetOpSessionId, active.TargetOpSessionId);
        Assert.IsNull(active.InvitationTokenHash);
        Assert.IsNotNull(active.SessionExpiresAt);
        Assert.IsNotNull(context.Session.GetString(UserAssistanceService.SessionKeyName));

        var overlay = await service.ResolveOverlayAsync(context, bobPrincipal);
        Assert.IsNotNull(overlay);
        Assert.AreEqual(alice.Id.ToString(), overlay.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.AreEqual(bobAccount.Id.ToString(),
            overlay.Principal.FindFirst(UserAssistanceClaimTypes.HelperUserAccountId)?.Value);
        Assert.AreEqual(active.Id.ToString(),
            overlay.Principal.FindFirst(UserAssistanceClaimTypes.SessionId)?.Value);

        // Put the target OP session and a token in the store so EndAsync must revoke them.
        var targetSession = await db.OpSessions.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.Id == targetOpSessionId);
        if (targetSession is null)
        {
            targetSession = new OpSession
            {
                Id = targetOpSessionId,
                TenantId = tenant.Id,
                UserId = alice.Id,
                AuthTime = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                IdleExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            db.OpSessions.Add(targetSession);
        }
        var targetToken = new Token
        {
            TenantId = tenant.Id,
            Type = "refresh",
            TokenHash = "assistance-refresh",
            UserId = alice.Id,
            ClientId = "client",
            SessionId = targetOpSessionId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.Tokens.Add(targetToken);
        await db.SaveChangesAsync();

        var ended = await service.EndAsync(context, alicePrincipal, active.Id, "Done");
        Assert.IsTrue(ended.Success, ended.Error);

        var endedRow = await db.UserAssistanceSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == active.Id);
        Assert.AreEqual(UserAssistanceStatus.Ended, endedRow.Status);
        Assert.IsNotNull((await db.OpSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == targetOpSessionId)).RevokedAt);
        Assert.IsNotNull((await db.Tokens.IgnoreQueryFilters().SingleAsync(t => t.Id == targetToken.Id)).RevokedAt);
        Assert.IsNull(context.Session.GetString(UserAssistanceService.SessionKeyName));
    }

    [TestMethod]
    public async Task ReadOnlyMiddleware_DeniesUnsafeMethods_AndAllowsReads()
    {
        var tenantId = Guid.NewGuid();
        var ctx = new DefaultHttpContext
        {
            User = Principal(Guid.NewGuid(), Guid.NewGuid())
        };
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/account/profile";

        var tenantAccessor = new TenantAccessor();
        tenantAccessor.SetTenant(new TenantContext { TenantId = tenantId, Slug = "tenant", Name = "Tenant" });

        var service = new Mock<IUserAssistanceService>();
        service.Setup(s => s.ResolveOverlayAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAssistanceOverlay
            {
                Session = new UserAssistanceSession
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Mode = UserAssistanceMode.ReadOnly,
                    Status = UserAssistanceStatus.Active,
                    SessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
                },
                RequesterUser = new User { TenantId = tenantId, Username = "requester" },
                RequesterAccount = new UserAccount { Username = "requester" },
                Principal = Principal(Guid.NewGuid(), Guid.NewGuid()),
                EffectiveRoleIds = []
            });

        var called = false;
        RequestDelegate next = _ =>
        {
            called = true;
            return Task.CompletedTask;
        };
        var middleware = new UserAssistanceMiddleware(next, NullLogger<UserAssistanceMiddleware>.Instance);

        await middleware.InvokeAsync(ctx, tenantAccessor, service.Object);

        Assert.AreEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.IsFalse(called);

        ctx.Request.Method = "GET";
        called = false;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await middleware.InvokeAsync(ctx, tenantAccessor, service.Object);
        Assert.IsTrue(called);
        Assert.AreEqual(StatusCodes.Status200OK, ctx.Response.StatusCode);

        // Control paths stay available to the actor even for unsafe methods.
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/account/assistance/stop";
        called = false;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await middleware.InvokeAsync(ctx, tenantAccessor, service.Object);
        Assert.IsTrue(called);
    }

    private static ClaimsPrincipal Principal(Guid userId, Guid accountId)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(UserClaimTypes.UserAccountId, accountId.ToString()),
                new Claim(ClaimTypes.Name, "user")
            ],
            "Test"));

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("idp.example");
        context.Request.Headers.UserAgent = "AssistanceTests";
        context.Session = new TestSession();
        return context;
    }

    private static AuthDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AuthDbContext(options);
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IEnumerable<string> Keys => _values.Keys;

        public void Clear() => _values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }
}
