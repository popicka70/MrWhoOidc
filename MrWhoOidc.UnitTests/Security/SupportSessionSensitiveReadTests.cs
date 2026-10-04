using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SupportAccess;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestDoubles;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Security.Admin;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Read-only support sessions: the request runs in the target tenant, and that tenant's query filters hid the
/// platform tenant's role assignment, so the platform-admin re-check always failed and support access never worked.
/// </summary>
[TestClass]
public sealed class SupportSessionSensitiveReadTests
{
    [TestMethod]
    [DataRow(TenantAdminOperationKind.Read, true)]
    [DataRow(TenantAdminOperationKind.Write, false)]
    [DataRow(TenantAdminOperationKind.SecuritySensitiveWrite, false)]
    public async Task ReadOnlySupportSession_AllowsOnlyPlainReads(TenantAdminOperationKind kind, bool expected)
    {
        var platformTenantId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var store = new StubTenantSupportAccessStore();
        var supportSession = new TenantSupportAccessSession
        {
            PlatformAdminUserAccountId = userId,
            TenantId = tenantId,
            Mode = SupportAccessMode.ReadOnly,
            Status = SupportAccessStatus.Active,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Reason = "ticket"
        };
        await store.CreateAsync(supportSession);

        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddLogging();
        services.AddScoped<ITenantAccessor>(_ => MockTenantAccessor.CreateWithTenant(tenantId, "acme"));
        services.AddScoped<IDefaultTenantContext>(_ => new FixedDefaultTenantContext(platformTenantId));
        services.AddScoped<ITenantSwitchingService, MrWhoOidc.UnitTests.MultiTenancy.MockTenantSwitchingService>();
        services.AddSingleton<ITenantSupportAccessStore>(store);
        services.AddSingleton<IAuditSink, NoopAuditSink>();
        services.AddSingleton<ITenantSupportAccessMetrics, NoopTenantSupportAccessMetrics>();
        services.AddOptions<TenantAdminAuthOptions>();
        services.AddOptions<PlatformAdminAuthOptions>();
        services.AddScoped<TenantAdminAuthorizationHandler>();

        var http = new DefaultHttpContext();
        var session = new Moq.Mock<ISession>();
        var sessionIdBytes = Encoding.UTF8.GetBytes(supportSession.Id.ToString());
        session.Setup(x => x.TryGetValue("SupportAccessSessionId", out sessionIdBytes)).Returns(true);
        http.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(new Microsoft.AspNetCore.Session.SessionFeature { Session = session.Object });
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http });

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        // Seed across tenants without the tenant write guard.
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options);
        var platformRealm = new Realm { TenantId = platformTenantId, Name = "platform" };
        var platformRole = new Role { TenantId = platformTenantId, RealmId = platformRealm.Id, Name = "platform-admin", IsActive = true };
        db.Tenants.Add(new Tenant { Id = tenantId, Slug = "acme", Name = "Acme", IssuerUri = "https://idp/t/acme", Status = TenantStatus.Active });
        db.Realms.Add(platformRealm);
        db.Roles.Add(platformRole);
        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = userId, RoleId = platformRole.Id, RealmId = platformRealm.Id, IsActive = true });
        await db.SaveChangesAsync();

        var handler = scope.ServiceProvider.GetRequiredService<TenantAdminAuthorizationHandler>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Cookies"));
        var requirement = new TenantAdminOperationRequirement { Kind = kind };
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        Assert.AreEqual(expected, context.HasSucceeded, $"read-only support session and {kind}");
    }

    private sealed class FixedDefaultTenantContext(Guid tenantId) : IDefaultTenantContext
    {
        public string DefaultTenantSlug => "default";
        public Task<Guid?> GetDefaultTenantIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(tenantId);
    }
}
