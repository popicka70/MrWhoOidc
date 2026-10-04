using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// H1 end to end: the interceptor must be wired into the real AuthDbContext, so a default-tenant tenant admin
/// cannot self-assign platform-admin through POST /admin/api/users/{id}/roles.
/// </summary>
[TestClass]
public sealed class PlatformRealmWriteGuardIntegrationTests
{
    private sealed class TenantAdminAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "tenant-admin")],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [TestMethod]
    public async Task TenantAdmin_CannotAssignPlatformAdmin_ButCanAssignOrdinaryRoles()
    {
        using var factory = ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory())
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TenantAdminAuthHandler>("Test", _ => { });
                    services.PostConfigure<AuthenticationOptions>(o =>
                    {
                        o.DefaultAuthenticateScheme = "Test";
                        o.DefaultChallengeScheme = "Test";
                    });
                    // Pass the tenant-admin gate; the real platform-admin policy stays in place.
                    services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(o =>
                        o.AddPolicy("tenant-admin", p => p.RequireAssertion(_ => true)));
                });
            });
        var client = factory.CreateClient();

        Guid userId, platformAdminRoleId, ordinaryRoleId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            // Startup creates the default tenant (without a platform realm) in this factory; reuse it.
            var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Slug == "default");
            if (tenant is null)
            {
                tenant = new Tenant { Slug = "default", Name = "Default", IssuerUri = "http://localhost/", Status = TenantStatus.Active };
                db.Tenants.Add(tenant);
            }

            scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
                .SetTenant(new TenantContext { TenantId = tenant.Id, Slug = tenant.Slug, Name = tenant.Name, IssuerUri = tenant.IssuerUri });
            var platformRealm = new Realm { TenantId = tenant.Id, Name = "platform" };
            var defaultRealm = new Realm { TenantId = tenant.Id, Name = "default" };
            var platformAdmin = new Role { TenantId = tenant.Id, RealmId = platformRealm.Id, Name = "platform-admin", IsActive = true };
            var ordinary = new Role { TenantId = tenant.Id, RealmId = defaultRealm.Id, Name = "h1-viewer", IsActive = true };
            var user = new User { TenantId = tenant.Id, Username = "h1-target", Email = "h1-target@example.com", NormalizedEmail = "h1-target@example.com" };
            db.Realms.AddRange(platformRealm, defaultRealm);
            db.Roles.AddRange(platformAdmin, ordinary);
            db.Users.Add(user);
            platformAdminRoleId = platformAdmin.Id;
            await db.SaveChangesAsync(); // outside a request: allowed
            (userId, ordinaryRoleId) = (user.Id, ordinary.Id);
        }

        var escalation = await client.PostAsJsonAsync($"/admin/api/users/{userId}/roles", new { roleId = platformAdminRoleId });
        var ordinaryAssign = await client.PostAsJsonAsync($"/admin/api/users/{userId}/roles", new { roleId = ordinaryRoleId });

        Assert.IsFalse(escalation.IsSuccessStatusCode, $"escalation must fail, got {(int)escalation.StatusCode}");
        Assert.AreEqual(HttpStatusCode.Created, ordinaryAssign.StatusCode);
        using var verify = factory.Services.CreateScope();
        var assignments = await verify.ServiceProvider.GetRequiredService<AuthDbContext>().UserRealmRoleAssignments
            .IgnoreQueryFilters().Where(a => a.UserId == userId).Select(a => a.RoleId).ToListAsync();
        CollectionAssert.DoesNotContain(assignments, platformAdminRoleId);
    }
}
