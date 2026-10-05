using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AdminBclOutboxApiTests
{
    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "admin@example.com"),
                new Claim(ClaimTypes.Role, "tenant-admin")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [TestMethod]
    public async Task TenantAdmin_OnUnprefixedAdminApi_SeesOnlyOwnTenantOutbox_AndRetryPersists()
    {
        using var factory = ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory())
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                    });
                    services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(options =>
                    {
                        options.AddPolicy("tenant-admin", policy => policy.RequireAssertion(_ => true));
                    });
                    // Operation markers are enforced on top of the policy; this test is about tenant scoping, not roles.
                    services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MrWhoOidc.UnitTests.TestDoubles.AllowTenantAdminOperationsHandler>();
                });
            });

        var client = factory.CreateClient();

        Guid ownId, foreignId;
        using (var scope = factory.Services.CreateScope())
        {
            var tenantId = scope.ServiceProvider.GetRequiredService<ITenantAccessor>().CurrentTenant!.TenantId;
            // Unscoped context (no tenant accessor) so the seed can write rows for two tenants.
            using var db = new AuthDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AuthDbContext>>());
            var own = NewNotification(tenantId, "own-client");
            var foreign = NewNotification(Guid.NewGuid(), "foreign-client");
            db.BackchannelLogoutNotifications.AddRange(own, foreign);
            await db.SaveChangesAsync();
            ownId = own.Id;
            foreignId = foreign.Id;
        }

        var list = await client.GetFromJsonAsync<JsonElement>("/admin/api/bcl/outbox");
        var ids = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        CollectionAssert.Contains(ids, ownId);
        CollectionAssert.DoesNotContain(ids, foreignId, "/admin/api is tenant-scoped; another tenant's outbox must not be listed.");

        var foreignRetry = await client.PostAsync($"/admin/api/bcl/outbox/{foreignId}/retry", content: null);
        Assert.AreEqual(HttpStatusCode.NotFound, foreignRetry.StatusCode);

        var ownRetry = await client.PostAsync($"/admin/api/bcl/outbox/{ownId}/retry", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, ownRetry.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var reloaded = await db.BackchannelLogoutNotifications.IgnoreQueryFilters().AsNoTracking().SingleAsync(n => n.Id == ownId);
            Assert.AreEqual("pending", reloaded.Status, "Retry must persist the status change.");
            var foreignReloaded = await db.BackchannelLogoutNotifications.IgnoreQueryFilters().AsNoTracking().SingleAsync(n => n.Id == foreignId);
            Assert.AreEqual("dead_letter", foreignReloaded.Status);
        }
    }

    private static BackchannelLogoutNotification NewNotification(Guid tenantId, string clientId) => new()
    {
        TenantId = tenantId,
        ClientDbId = Guid.NewGuid(),
        ClientId = clientId,
        TargetUri = "https://rp.example.com/bcl",
        LogoutToken = "x.y.z",
        Status = "dead_letter",
        AttemptCount = 5
    };
}
