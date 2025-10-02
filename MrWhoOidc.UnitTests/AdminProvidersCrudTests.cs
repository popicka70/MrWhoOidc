using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for admin Provider CRUD operations - validation, updates, deletions.
/// Complements AdminProvidersApiTests (which covers create and get).
/// </summary>
[TestClass]
public sealed class AdminProvidersCrudTests
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
        var dbName = "admin-crud-test-" + Guid.NewGuid().ToString("N");
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
                        opts.AddPolicy("admin", p => p.RequireAssertion(_ => true));
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.UseEndpoints(endpoints =>
                    {
                        var admin = endpoints.MapGroup("/admin/api").RequireAuthorization("admin");
                        
                        admin.MapGet("/providers", async (AuthDbContext db) =>
                        {
                            var list = await db.IdentityProviders.AsNoTracking()
                                .OrderBy(p => p.Name)
                                .ToListAsync();
                            return Results.Ok(list);
                        });

                        admin.MapPost("/providers", async (AuthDbContext db, IdentityProvider input) =>
                        {
                            // Basic validation
                            if (string.IsNullOrWhiteSpace(input.Name))
                                return Results.Problem(statusCode: 400, title: "Validation failed", detail: "Name is required");

                            input.Id = Guid.NewGuid();
                            input.CreatedAt = DateTimeOffset.UtcNow;
                            input.UpdatedAt = DateTimeOffset.UtcNow;
                            db.IdentityProviders.Add(input);
                            await db.SaveChangesAsync();
                            return Results.Created($"/admin/api/providers/{input.Id}", new { input.Id });
                        });

                        admin.MapPut("/providers/{id:guid}", async (Guid id, AuthDbContext db, IdentityProvider input) =>
                        {
                            var entity = await db.IdentityProviders.FindAsync(id);
                            if (entity is null) return Results.NotFound();

                            entity.Name = input.Name;
                            entity.DisplayName = input.DisplayName;
                            entity.Type = input.Type;
                            entity.Enabled = input.Enabled;
                            entity.ConfigJson = input.ConfigJson;
                            entity.UpdatedAt = DateTimeOffset.UtcNow;
                            await db.SaveChangesAsync();
                            return Results.NoContent();
                        });

                        admin.MapDelete("/providers/{id:guid}", async (Guid id, AuthDbContext db) =>
                        {
                            var entity = await db.IdentityProviders.FindAsync(id);
                            if (entity is null) return Results.NotFound();
                            db.IdentityProviders.Remove(entity);
                            await db.SaveChangesAsync();
                            return Results.NoContent();
                        });
                    });
                });
            });

        var host = await builder.StartAsync();
        TestAuthHandler.CurrentUserId = userId;
        return host;
    }

    [TestMethod]
    public async Task Admin_Providers_List_Returns_All()
    {
        // Arrange
        var userId = Guid.NewGuid();
        using var host = await CreateHostAsync(userId);
        
        // Seed providers
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.IdentityProviders.Add(new IdentityProvider
            {
                Id = Guid.NewGuid(),
                Name = "provider1",
                DisplayName = "Provider 1",
                Type = IdentityProviderType.Oidc,
                Enabled = true,
                ConfigJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.IdentityProviders.Add(new IdentityProvider
            {
                Id = Guid.NewGuid(),
                Name = "provider2",
                DisplayName = "Provider 2",
                Type = IdentityProviderType.Oidc,
                Enabled = false,
                ConfigJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        // Act
        var response = await client.GetAsync("/admin/api/providers");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(json.Contains("provider1"));
        Assert.IsTrue(json.Contains("provider2"));
    }

    [TestMethod]
    public async Task Admin_Providers_Create_Validation_Rejects_Empty_Name()
    {
        // Arrange
        var userId = Guid.NewGuid();
        using var host = await CreateHostAsync(userId);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        var invalidProvider = new
        {
            Name = "",  // Empty name should fail validation
            DisplayName = "Invalid",
            Type = 0,
            Enabled = true,
            ConfigJson = "{}"
        };

        // Act
        var response = await client.PostAsJsonAsync("/admin/api/providers", invalidProvider);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
            "Expected 400 for provider with empty name");
    }

    [TestMethod]
    public async Task Admin_Providers_Update_Modifies_Fields()
    {
        // Arrange
        var userId = Guid.NewGuid();
        using var host = await CreateHostAsync(userId);
        var providerId = Guid.NewGuid();

        // Seed original provider
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.IdentityProviders.Add(new IdentityProvider
            {
                Id = providerId,
                Name = "original",
                DisplayName = "Original",
                Type = IdentityProviderType.Oidc,
                Enabled = true,
                ConfigJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        var updated = new
        {
            Name = "updated",
            DisplayName = "Updated Name",
            Type = 0,
            Enabled = false,
            ConfigJson = "{\"updated\":true}"
        };

        // Act
        var response = await client.PutAsJsonAsync($"/admin/api/providers/{providerId}", updated);

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        // Verify changes
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var saved = await db.IdentityProviders.FindAsync(providerId);
            Assert.IsNotNull(saved);
            Assert.AreEqual("updated", saved.Name);
            Assert.AreEqual("Updated Name", saved.DisplayName);
            Assert.IsFalse(saved.Enabled);
        }
    }

    [TestMethod]
    public async Task Admin_Providers_Update_Returns_NotFound_For_Missing_Id()
    {
        // Arrange
        var userId = Guid.NewGuid();
        using var host = await CreateHostAsync(userId);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        var nonExistentId = Guid.NewGuid();
        var updated = new
        {
            Name = "updated",
            DisplayName = "Updated",
            Type = 0,
            Enabled = true,
            ConfigJson = "{}"
        };

        // Act
        var response = await client.PutAsJsonAsync($"/admin/api/providers/{nonExistentId}", updated);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Admin_Providers_Delete_Removes_Provider()
    {
        // Arrange
        var userId = Guid.NewGuid();
        using var host = await CreateHostAsync(userId);
        var providerId = Guid.NewGuid();

        // Seed provider
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.IdentityProviders.Add(new IdentityProvider
            {
                Id = providerId,
                Name = "to-delete",
                DisplayName = "To Delete",
                Type = IdentityProviderType.Oidc,
                Enabled = true,
                ConfigJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        // Act
        var response = await client.DeleteAsync($"/admin/api/providers/{providerId}");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        // Verify deletion
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var deleted = await db.IdentityProviders.FindAsync(providerId);
            Assert.IsNull(deleted, "Provider should be deleted");
        }
    }

    [TestMethod]
    public async Task Admin_Providers_Delete_Returns_NotFound_For_Missing_Id()
    {
        // Arrange
        var userId = Guid.NewGuid();
        using var host = await CreateHostAsync(userId);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await client.DeleteAsync($"/admin/api/providers/{nonExistentId}");

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
