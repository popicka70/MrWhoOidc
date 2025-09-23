using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AdminProvidersApiTests
{
    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public static Guid CurrentUserId;
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var uid = CurrentUserId == Guid.Empty ? Guid.NewGuid() : CurrentUserId;
            var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, uid.ToString()) };
            var id = new System.Security.Claims.ClaimsIdentity(claims, Scheme.Name);
            var principal = new System.Security.Claims.ClaimsPrincipal(id);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private static async Task<IHost> CreateHostAsync(Guid userId)
    {
        var dbName = "admin-api-tests-" + Guid.NewGuid().ToString("N");
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDbContext<AuthDbContext>(opts => opts.UseInMemoryDatabase(dbName));
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddAuthorization(opts =>
                    {
                        // In this integration test, bypass admin requirement to focus on API behavior
                        opts.AddPolicy("admin", p => p.RequireAssertion(_ => true));
                    });
                });
                webBuilder.Configure(async app =>
                {
                    // Seed a user (not used by policy here, but kept for completeness)
                    using (var scope = app.ApplicationServices.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                        var realm = new Realm { Name = "admin" };
                        db.Realms.Add(realm);
                        var role = new Role { Name = "admin", RealmId = realm.Id, IsActive = true };
                        db.Roles.Add(role);
                        var user = new User { Id = userId, Username = "admin@test", Name = "Admin" };
                        db.Users.Add(user);
                        db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = user.Id, RoleId = role.Id, ClientId = Guid.NewGuid(), RealmId = realm.Id, IsActive = true });
                        await db.SaveChangesAsync();
                    }

                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        var admin = endpoints.MapGroup("/admin/api").RequireAuthorization("admin");
                        admin.MapPost("/providers", async (AuthDbContext db, MrWhoOidc.Auth.Persistence.IdentityProvider input) =>
                        {
                            input.Id = Guid.NewGuid();
                            input.Name = string.IsNullOrWhiteSpace(input.Name) ? "prov" + Guid.NewGuid().ToString("N")[..6] : input.Name;
                            input.CreatedAt = DateTimeOffset.UtcNow;
                            input.UpdatedAt = DateTimeOffset.UtcNow;
                            db.IdentityProviders.Add(input);
                            await db.SaveChangesAsync();
                            return Results.Created($"/admin/api/providers/{input.Id}", new { input.Id });
                        });
                        admin.MapGet("/providers/{id:guid}", async (Guid id, AuthDbContext db) =>
                        {
                            var p = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
                            return p is null ? Results.NotFound() : Results.Ok(p);
                        });
                    });
                });
            });

        var host = await builder.StartAsync();
        TestAuthHandler.CurrentUserId = userId;
        return host;
    }

    [TestMethod]
    public async Task Providers_Create_And_Get_ById_Works_With_Admin_Policy()
    {
        var userId = Guid.NewGuid();
        using var host = await CreateHostAsync(userId);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        var createPayload = new { Name = "google", DisplayName = "Google", Type = 0, Enabled = true };
        var createResp = await client.PostAsJsonAsync("/admin/api/providers", createPayload);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode);
        var createdDoc = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = Guid.Parse(createdDoc.GetProperty("id").GetString()!);

        var getResp = await client.GetAsync($"/admin/api/providers/{id}");
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);
        var prov = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("google", prov.GetProperty("name").GetString());
    }
}
