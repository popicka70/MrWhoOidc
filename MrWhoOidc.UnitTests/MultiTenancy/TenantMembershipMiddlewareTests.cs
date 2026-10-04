using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SupportAccess;
using MrWhoOidc.WebAuth.Middleware;

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// H4 of the 2026-10-04 post-Phase-0 review: the "users may only access tenants they are a member of" check ran
/// in TenantResolutionMiddleware, before UseAuthentication(), so context.User was always anonymous and the check
/// never fired. A tenant-A session was accepted on every /t/B/... route.
/// </summary>
[TestClass]
public sealed class TenantMembershipMiddlewareTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private sealed class DictionarySession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new();
        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString();
        public IEnumerable<string> Keys => _values.Keys;
        public void Clear() => _values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value) => _values.TryGetValue(key, out value);
    }

    private sealed class SessionFeature(ISession session) : ISessionFeature
    {
        public ISession Session { get; set; } = session;
    }

    private static async Task<(AuthDbContext Db, User AliceInA)> SeedAsync()
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var alice = new User { TenantId = TenantA, Username = "alice" };
        db.Users.Add(alice);
        await db.SaveChangesAsync();
        return (db, alice);
    }

    private static async Task<ClaimsPrincipal?> RunAsync(AuthDbContext db, Guid? requestTenant, Guid subject, TenantSupportAccessSession? supportSession = null)
    {
        var accessor = new TenantAccessor();
        if (requestTenant is { } t)
        {
            accessor.SetTenant(new TenantContext { TenantId = t, Slug = "b", IssuerUri = "https://issuer/t/b" });
        }

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject.ToString())], "Cookies")),
        };
        var session = new DictionarySession();
        http.Features.Set<ISessionFeature>(new SessionFeature(session));
        if (supportSession is not null)
        {
            db.TenantSupportAccessSessions.Add(supportSession);
            await db.SaveChangesAsync();
            session.SetString(TenantMembershipMiddleware.SupportAccessSessionKey, supportSession.Id.ToString());
        }

        ClaimsPrincipal? seen = null;
        var middleware = new TenantMembershipMiddleware(ctx => { seen = ctx.User; return Task.CompletedTask; }, NullLogger<TenantMembershipMiddleware>.Instance);
        await middleware.InvokeAsync(http, accessor, db, new TenantSupportAccessStore(db));
        return seen;
    }

    [TestMethod]
    public async Task UserOfAnotherTenant_ContinuesAsAnonymous()
    {
        var (db, alice) = await SeedAsync();
        using var _ = db;

        var user = await RunAsync(db, TenantB, alice.Id);

        Assert.IsFalse(user?.Identity?.IsAuthenticated ?? false, "a tenant-A session must not be honoured on tenant B");
    }

    [TestMethod]
    public async Task UserOfTheRequestedTenant_IsKept()
    {
        var (db, alice) = await SeedAsync();
        using var _ = db;

        var user = await RunAsync(db, TenantA, alice.Id);

        Assert.IsTrue(user?.Identity?.IsAuthenticated);
    }

    [TestMethod]
    public async Task SubjectThatIsNotAUser_IsKept()
    {
        var (db, _) = await SeedAsync();
        using var __ = db;

        // client_credentials tokens and pairwise subjects do not name a User row; authorization decides.
        var user = await RunAsync(db, TenantB, Guid.NewGuid());

        Assert.IsTrue(user?.Identity?.IsAuthenticated);
    }

    [TestMethod]
    public async Task RouteWithoutTenant_IsUntouched()
    {
        var (db, alice) = await SeedAsync();
        using var _ = db;

        var user = await RunAsync(db, requestTenant: null, alice.Id);

        Assert.IsTrue(user?.Identity?.IsAuthenticated);
    }

    [TestMethod]
    public async Task ActiveSupportSessionForThisTenant_KeepsThePlatformAdmin()
    {
        var (db, alice) = await SeedAsync();
        using var _ = db;
        var support = new TenantSupportAccessSession { TenantId = TenantB, PlatformAdminUserAccountId = alice.Id, Reason = "ticket", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) };

        var user = await RunAsync(db, TenantB, alice.Id, support);

        Assert.IsTrue(user?.Identity?.IsAuthenticated);
    }

    [TestMethod]
    public async Task ExpiredSupportSession_DoesNotKeepTheIdentity()
    {
        var (db, alice) = await SeedAsync();
        using var _ = db;
        var support = new TenantSupportAccessSession { TenantId = TenantB, PlatformAdminUserAccountId = alice.Id, Reason = "ticket", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

        var user = await RunAsync(db, TenantB, alice.Id, support);

        Assert.IsFalse(user?.Identity?.IsAuthenticated ?? false);
    }

    [TestMethod]
    public async Task SupportSessionOfAnotherActor_DoesNotKeepTheIdentity()
    {
        var (db, alice) = await SeedAsync();
        using var _ = db;
        var support = new TenantSupportAccessSession { TenantId = TenantB, PlatformAdminUserAccountId = Guid.NewGuid(), Reason = "ticket", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) };

        var user = await RunAsync(db, TenantB, alice.Id, support);

        Assert.IsFalse(user?.Identity?.IsAuthenticated ?? false);
    }
}
