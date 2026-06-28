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
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class PlatformUnassignedUsersApiTests
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
                new Claim(ClaimTypes.Name, "platform-admin@example.com"),
                new Claim(ClaimTypes.Role, "platform-admin")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    [TestMethod]
    public async Task PlatformAdmin_Can_List_And_Terminate_Unassigned_UserAccounts()
    {
        using var factory = CreateFactory();
        Guid unassignedId;
        Guid assignedId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var tenant = await db.Tenants.FirstAsync();
            var unassigned = new UserAccount
            {
                Username = "unassigned@example.com",
                Email = "unassigned@example.com",
                NormalizedEmail = "unassigned@example.com",
                Name = "Unassigned Example",
                PasswordHash = "test"
            };
            var assigned = new UserAccount
            {
                Username = "assigned@example.com",
                Email = "assigned@example.com",
                NormalizedEmail = "assigned@example.com",
                Name = "Assigned Example",
                PasswordHash = "test"
            };

            db.UserAccounts.AddRange(unassigned, assigned);
            db.UserTenantMemberships.Add(new UserTenantMembership
            {
                UserAccountId = assigned.Id,
                TenantId = tenant.Id,
                Status = TenantMembershipStatus.Active
            });
            await db.SaveChangesAsync();

            unassignedId = unassigned.Id;
            assignedId = assigned.Id;
        }

        var client = factory.CreateClient();
        var list = await client.GetFromJsonAsync<JsonElement>("/platform-admin/api/users/unassigned?search=example.com");
        var items = list.GetProperty("items").EnumerateArray().ToArray();
        Assert.IsTrue(items.Any(item => item.GetProperty("id").GetGuid() == unassignedId));
        Assert.IsFalse(items.Any(item => item.GetProperty("id").GetGuid() == assignedId));

        var getAssigned = await client.GetAsync($"/platform-admin/api/users/unassigned/{assignedId}");
        Assert.AreEqual(HttpStatusCode.NotFound, getAssigned.StatusCode);

        var deleteAssigned = await client.DeleteAsync($"/platform-admin/api/users/unassigned/{assignedId}");
        Assert.AreEqual(HttpStatusCode.Conflict, deleteAssigned.StatusCode);

        var deleteUnassigned = await client.DeleteAsync($"/platform-admin/api/users/unassigned/{unassignedId}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteUnassigned.StatusCode);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.IsFalse(await verifyDb.UserAccounts.AnyAsync(account => account.Id == unassignedId));
        Assert.IsTrue(await verifyDb.UserAccounts.AnyAsync(account => account.Id == assignedId));
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory())
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
                        options.AddPolicy("platform-admin", policy => policy.RequireAssertion(_ => true));
                    });
                });
            });
    }
}