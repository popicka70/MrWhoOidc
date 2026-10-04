using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// H1 of the 2026-10-04 post-Phase-0 review: a tenant admin of the default tenant could assign itself the
/// platform-admin role (or create/rename roles into the platform realm) because role writes only checked
/// that the role belonged to the caller's tenant.
/// </summary>
[TestClass]
public sealed class PlatformRealmWriteGuardTests
{
    private static readonly Guid DefaultTenant = Guid.NewGuid();
    private static readonly Guid OtherTenant = Guid.NewGuid();

    // HttpContextAccessor keeps the context in an AsyncLocal, which would not flow out of the async fixture.
    private sealed class FieldHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class Fixture : IDisposable
    {
        public required AuthDbContext Db { get; init; }
        public required FieldHttpContextAccessor Accessor { get; init; }
        public required Realm PlatformRealm { get; init; }
        public required Realm DefaultRealm { get; init; }
        public required Role PlatformAdminRole { get; init; }
        public required Role ViewerRole { get; init; }
        public Guid UserId { get; } = Guid.NewGuid();
        public void Dispose() => Db.Dispose();
    }

    private static async Task<Fixture> CreateAsync(bool callerIsPlatformAdmin)
    {
        var accessor = new FieldHttpContextAccessor();
        var guard = new PlatformRealmWriteGuard(accessor, Options.Create(new PlatformAdminAuthOptions()));
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(guard)
            .Options);

        var platformRealm = new Realm { TenantId = DefaultTenant, Name = "platform" };
        var defaultRealm = new Realm { TenantId = DefaultTenant, Name = "default" };
        var platformAdmin = new Role { TenantId = DefaultTenant, Name = "platform-admin", RealmId = platformRealm.Id, IsActive = true };
        var viewer = new Role { TenantId = DefaultTenant, Name = "viewer", RealmId = defaultRealm.Id, IsActive = true };
        db.Realms.AddRange(platformRealm, defaultRealm);
        db.Roles.AddRange(platformAdmin, viewer);
        await db.SaveChangesAsync(); // no HTTP request yet: seeding is allowed
        db.ChangeTracker.Clear();

        var defaultTenant = new Mock<IDefaultTenantContext>();
        defaultTenant.Setup(d => d.GetDefaultTenantIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(DefaultTenant);
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), "platform-admin"))
            .ReturnsAsync(callerIsPlatformAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(defaultTenant.Object)
                .AddSingleton(authorization.Object)
                .BuildServiceProvider(),
        };

        return new Fixture { Db = db, Accessor = accessor, PlatformRealm = platformRealm, DefaultRealm = defaultRealm, PlatformAdminRole = platformAdmin, ViewerRole = viewer };
    }

    [TestMethod]
    public async Task TenantAdmin_AssigningPlatformAdminRole_IsRefused()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        f.Db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = f.UserId, RoleId = f.PlatformAdminRole.Id, RealmId = f.PlatformRealm.Id, IsActive = true });

        await Assert.ThrowsExactlyAsync<PlatformRealmWriteDeniedException>(() => f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task TenantAdmin_AssignmentPointingAtPlatformRoleThroughAnotherRealm_IsRefused()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        f.Db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = f.UserId, RoleId = f.PlatformAdminRole.Id, RealmId = f.DefaultRealm.Id, IsActive = true });

        await Assert.ThrowsExactlyAsync<PlatformRealmWriteDeniedException>(() => f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task TenantAdmin_CreatingRoleInPlatformRealm_IsRefused()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        f.Db.Roles.Add(new Role { TenantId = DefaultTenant, Name = "sneaky", RealmId = f.PlatformRealm.Id });

        await Assert.ThrowsExactlyAsync<PlatformRealmWriteDeniedException>(() => f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task TenantAdmin_RenamingRealmIntoPlatform_IsRefused()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        var realm = await f.Db.Realms.SingleAsync(r => r.Id == f.DefaultRealm.Id);
        realm.Name = "platform";

        await Assert.ThrowsExactlyAsync<PlatformRealmWriteDeniedException>(() => f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task TenantAdmin_MovingPlatformRoleOutOfThePlatformRealm_IsRefused()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        var role = await f.Db.Roles.SingleAsync(r => r.Id == f.PlatformAdminRole.Id);
        role.RealmId = f.DefaultRealm.Id;

        await Assert.ThrowsExactlyAsync<PlatformRealmWriteDeniedException>(() => f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task TenantAdmin_OrdinaryRoleAssignment_IsAllowed()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        f.Db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = f.UserId, RoleId = f.ViewerRole.Id, RealmId = f.DefaultRealm.Id, IsActive = true });

        Assert.AreEqual(1, await f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task OtherTenantsPlatformNamedRealm_IsNotProtected()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        f.Db.Realms.Add(new Realm { TenantId = OtherTenant, Name = "platform" });

        Assert.AreEqual(1, await f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task PlatformAdmin_AssigningPlatformAdminRole_IsAllowed()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: true);
        f.Db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = f.UserId, RoleId = f.PlatformAdminRole.Id, RealmId = f.PlatformRealm.Id, IsActive = true });

        Assert.AreEqual(1, await f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task BootstrapMarkedRequest_IsAllowed()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: false);
        PlatformRealmWriteGuard.AllowSystemWrite(f.Accessor.HttpContext!);
        f.Db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = f.UserId, RoleId = f.PlatformAdminRole.Id, RealmId = f.PlatformRealm.Id, IsActive = true });

        Assert.AreEqual(1, await f.Db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task SynchronousSave_OfRoleChangesInARequest_FailsClosed()
    {
        using var f = await CreateAsync(callerIsPlatformAdmin: true);
        f.Db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = f.UserId, RoleId = f.ViewerRole.Id, RealmId = f.DefaultRealm.Id, IsActive = true });

        Assert.ThrowsExactly<PlatformRealmWriteDeniedException>(() => f.Db.SaveChanges());
    }
}
