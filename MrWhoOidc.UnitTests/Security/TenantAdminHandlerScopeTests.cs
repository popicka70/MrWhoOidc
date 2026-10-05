using System.Security.Claims;
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
/// TenantAdminAuthorizationHandler derives from AuthorizationHandler&lt;IAuthorizationRequirement&gt;, so it ran for
/// every requirement of every policy and succeeded it for any tenant admin of the current tenant: a tenant admin
/// passed "platform-admin" (and "admin").
/// </summary>
[TestClass]
public sealed class TenantAdminHandlerScopeTests
{
    [TestMethod]
    [DataRow("platform-admin")]
    [DataRow("admin")]
    public async Task TenantAdmin_DoesNotSatisfyOtherPolicies(string policy)
    {
        var platformTenantId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AuthDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("admin", p => p.Requirements.Add(new AdminRequirement()));
            options.AddPolicy("platform-admin", p => p.Requirements.Add(new PlatformAdminRequirement()));
            options.AddPolicy("tenant-admin", p => p.Requirements.Add(new TenantAdminRequirement()));
        });
        services.AddScoped<ITenantAccessor>(_ => MockTenantAccessor.CreateWithTenant(tenantId, "acme"));
        services.AddScoped<IDefaultTenantContext>(_ => new FixedDefaultTenantContext(platformTenantId));
        services.AddScoped<ITenantSwitchingService, MrWhoOidc.UnitTests.MultiTenancy.MockTenantSwitchingService>();
        services.AddScoped<ITenantSupportAccessStore>(_ => new StubTenantSupportAccessStore());
        services.AddSingleton<MrWhoOidc.WebAuth.Observability.IAuditSink, MrWhoOidc.WebAuth.Observability.NoopAuditSink>();
        services.AddSingleton<ITenantSupportAccessMetrics, NoopTenantSupportAccessMetrics>();
        services.AddOptions<TenantAdminAuthOptions>();
        services.AddOptions<PlatformAdminAuthOptions>();
        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, TenantAdminAuthorizationHandler>();
        var http = new DefaultHttpContext();
        var session = new Moq.Mock<ISession>();
        byte[]? none = null;
        session.Setup(x => x.TryGetValue(Moq.It.IsAny<string>(), out none)).Returns(false);
        http.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(new Microsoft.AspNetCore.Session.SessionFeature { Session = session.Object });
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http });
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var realm = new Realm { TenantId = tenantId, Name = "default" };
        var role = new Role { TenantId = tenantId, RealmId = realm.Id, Name = "tenant-admin", IsActive = true };
        db.Tenants.Add(new Tenant { Id = tenantId, Slug = "acme", Name = "Acme", IssuerUri = "https://idp/t/acme", Status = TenantStatus.Active });
        db.Users.Add(new User { Id = userId, TenantId = tenantId, Username = "tenant-admin-user" });
        db.Realms.Add(realm);
        db.Roles.Add(role);
        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment { UserId = userId, RoleId = role.Id, RealmId = realm.Id, IsActive = true });
        await db.SaveChangesAsync();

        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Cookies"));

        Assert.IsTrue((await authorization.AuthorizeAsync(principal, "tenant-admin")).Succeeded, "sanity: the user is a tenant admin");
        Assert.IsFalse((await authorization.AuthorizeAsync(principal, policy)).Succeeded, $"a tenant admin must not pass '{policy}'");
    }

    private sealed class FixedDefaultTenantContext(Guid tenantId) : IDefaultTenantContext
    {
        public string DefaultTenantSlug => "default";
        public Task<Guid?> GetDefaultTenantIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(tenantId);
    }
}
