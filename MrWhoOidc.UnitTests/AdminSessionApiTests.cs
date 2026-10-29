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
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// F1: the tenant admin API lists the OP sessions of a user and can end one of them. The endpoints are
/// tenant-scoped, the unsafe method is antiforgery protected, and the revocation is audited.
/// </summary>
[TestClass]
public sealed class AdminSessionApiTests
{
    private const string AdminTestUserId = "11111111-1111-1111-1111-111111111111";

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, AdminTestUserId),
                new Claim(ClaimTypes.Name, "admin@example.com"),
                new Claim(ClaimTypes.Role, "tenant-admin")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [TestMethod]
    public async Task TheSessionsOfTheUser_AreListed_AndTheOtherTenantIsNotVisible()
    {
        using var factory = CreateFactory();
        var client = factory.CreateSecureClient();

        Guid userId, ownSessionId, foreignSessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var tenantId = scope.ServiceProvider.GetRequiredService<ITenantAccessor>().CurrentTenant!.TenantId;
            using var db = new AuthDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AuthDbContext>>());

            var user = new User { TenantId = tenantId, Username = "alice", Email = "alice@example.com" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;

            var now = DateTimeOffset.UtcNow;
            var own = new OpSession
            {
                TenantId = tenantId,
                UserId = userId,
                AuthTime = now,
                LastSeenAt = now,
                ExpiresAt = now.AddHours(8),
                IdleExpiresAt = now.AddMinutes(30),
                UserAgent = "TestBrowser/1.0"
            };
            var foreign = new OpSession
            {
                TenantId = Guid.NewGuid(),
                UserId = userId,
                AuthTime = now,
                LastSeenAt = now,
                ExpiresAt = now.AddHours(8),
                IdleExpiresAt = now.AddMinutes(30)
            };
            db.OpSessions.AddRange(own, foreign);
            await db.SaveChangesAsync();
            ownSessionId = own.Id;
            foreignSessionId = foreign.Id;
        }

        var list = await client.GetFromJsonAsync<JsonElement>($"/admin/api/users/{userId}/sessions");
        var ids = list.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();

        CollectionAssert.Contains(ids, ownSessionId);
        CollectionAssert.DoesNotContain(ids, foreignSessionId, "the admin API is tenant-scoped");
    }

    [TestMethod]
    public async Task DeletingASession_RevokesIt_AndItsRefreshTokens()
    {
        using var factory = CreateFactory();
        var client = factory.CreateSecureClient();

        Guid userId, sessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var tenantId = scope.ServiceProvider.GetRequiredService<ITenantAccessor>().CurrentTenant!.TenantId;
            using var db = new AuthDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AuthDbContext>>());

            var user = new User { TenantId = tenantId, Username = "bob" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;

            var now = DateTimeOffset.UtcNow;
            var session = new OpSession
            {
                TenantId = tenantId,
                UserId = userId,
                AuthTime = now,
                LastSeenAt = now,
                ExpiresAt = now.AddHours(8),
                IdleExpiresAt = now.AddMinutes(30)
            };
            db.OpSessions.Add(session);
            db.Tokens.Add(new Token
            {
                Type = "refresh",
                TokenHash = "hash-" + Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
                UserId = userId,
                ClientId = "c1",
                SessionId = session.Id,
                ExpiresAt = now.AddDays(1)
            });
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/api/users/{userId}/sessions/{sessionId}");
        TestAntiforgeryHelper.AddToken(client, request, factory.Services, AdminTestUserId, "admin@example.com");
        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var session = await db.OpSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId);
            Assert.IsNotNull(session.RevokedAt, "the session is revoked");

            var token = await db.Tokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.SessionId == sessionId);
            Assert.IsNotNull(token.RevokedAt, "the refresh tokens of the session are revoked with it");
        }
    }

    [TestMethod]
    public async Task DeletingASessionOfAnotherUser_IsNotFound()
    {
        using var factory = CreateFactory();
        var client = factory.CreateSecureClient();

        Guid otherUserId, sessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var tenantId = scope.ServiceProvider.GetRequiredService<ITenantAccessor>().CurrentTenant!.TenantId;
            using var db = new AuthDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AuthDbContext>>());

            var other = new User { TenantId = tenantId, Username = "carol" };
            db.Users.Add(other);
            await db.SaveChangesAsync();
            otherUserId = other.Id;

            var now = DateTimeOffset.UtcNow;
            var session = new OpSession
            {
                TenantId = tenantId,
                UserId = other.Id,
                AuthTime = now,
                LastSeenAt = now,
                ExpiresAt = now.AddHours(8),
                IdleExpiresAt = now.AddMinutes(30)
            };
            db.OpSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/api/users/{otherUserId}/sessions/{Guid.NewGuid()}");
        TestAntiforgeryHelper.AddToken(client, request, factory.Services, AdminTestUserId, "admin@example.com");
        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task TheUnsafeMethod_WithoutAnAntiforgeryToken_IsRefused()
    {
        using var factory = CreateFactory();
        var client = factory.CreateSecureClient();

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/api/users/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}");
        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, "M-CSRF: the unsafe method needs the antiforgery token");
    }

    private static WebApplicationFactory<Program> CreateFactory()
        => ((WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory())
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
                    services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MrWhoOidc.UnitTests.TestDoubles.AllowTenantAdminOperationsHandler>();
                });
            });
}
