using System.Net;
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

namespace MrWhoOidc.UnitTests.MultiTenancy;

/// <summary>
/// H4 wiring: the membership check must sit between authentication and authorization in the real pipeline.
/// </summary>
[TestClass]
public sealed class TenantMembershipPipelineTests
{
    private const string UserHeader = "X-Test-User";

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var id))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [TestMethod]
    public async Task SessionFromAnotherTenant_IsNotAuthenticatedOnThisTenant()
    {
        using var factory = ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory())
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
                    services.PostConfigure<AuthenticationOptions>(o =>
                    {
                        o.DefaultAuthenticateScheme = "Test";
                        o.DefaultChallengeScheme = "Test";
                    });
                    // Isolate the membership check: any authenticated user may call the endpoint.
                    services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(o =>
                        o.AddPolicy("tenant-admin", p => p.RequireAuthenticatedUser()));
                    services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MrWhoOidc.UnitTests.TestDoubles.SatisfyTenantAdminOperationHandler>();
                });
            });
        var client = factory.CreateClient();
        await client.GetAsync("/.well-known/openid-configuration"); // let startup create the default tenant

        Guid localUser, foreignUser;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var home = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Slug == "default");
            var other = new Tenant { Slug = "other", Name = "Other", IssuerUri = "http://localhost/t/other", Status = TenantStatus.Active };
            var accessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();

            accessor.SetTenant(new TenantContext { TenantId = home.Id, Slug = home.Slug, IssuerUri = home.IssuerUri });
            var local = new User { TenantId = home.Id, Username = "local" };
            db.Tenants.Add(other);
            db.Users.Add(local);
            await db.SaveChangesAsync();

            accessor.SetTenant(new TenantContext { TenantId = other.Id, Slug = other.Slug, IssuerUri = other.IssuerUri });
            var foreign = new User { TenantId = other.Id, Username = "foreign" };
            db.Users.Add(foreign);
            await db.SaveChangesAsync();
            (localUser, foreignUser) = (local.Id, foreign.Id);
        }

        var asLocal = new HttpRequestMessage(HttpMethod.Get, "/admin/api/invitations");
        asLocal.Headers.Add(UserHeader, localUser.ToString());
        var asForeign = new HttpRequestMessage(HttpMethod.Get, "/admin/api/invitations");
        asForeign.Headers.Add(UserHeader, foreignUser.ToString());

        var localResponse = await client.SendAsync(asLocal);
        var foreignResponse = await client.SendAsync(asForeign);

        Assert.AreEqual(HttpStatusCode.OK, localResponse.StatusCode, "a member of the tenant keeps its session");
        Assert.IsFalse(foreignResponse.IsSuccessStatusCode, $"another tenant's session must not be honoured, got {(int)foreignResponse.StatusCode}");
    }
}
