using LicensingService.Core.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace LicensingService.Tests.Integration;

/// <summary>
/// Web application factory for integration tests with in-memory SQLite database and test authentication.
/// </summary>
public class LicensingServiceWebApplicationFactory : WebApplicationFactory<Program>
{
    private DbConnection? _connection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Remove the existing DbContext options
            var dbContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<LicensingDbContext>));
            if (dbContextDescriptor != null)
            {
                services.Remove(dbContextDescriptor);
            }

            // Create and open an in-memory SQLite connection
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            // Add DbContext using the in-memory connection
            services.AddDbContext<LicensingDbContext>((container, options) =>
            {
                options.UseSqlite(_connection);
                options.EnableSensitiveDataLogging();
            });

            // Replace authentication with test scheme
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "TestScheme";
                options.DefaultChallengeScheme = "TestScheme";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", options => { });

            // Build provider to ensure database is created
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
            db.Database.EnsureCreated();
        });

        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection?.Dispose();
        }
    }

    public HttpClient CreateAuthenticatedClient(string userId = "test-user", string? role = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", userId);
        if (!string.IsNullOrEmpty(role))
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Role", role);
        }
        return client;
    }
}

/// <summary>
/// Test authentication handler that creates a test user from headers.
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for test user headers
        if (!Request.Headers.TryGetValue("X-Test-User-Id", out var userIdValues))
        {
            return Task.FromResult(AuthenticateResult.Fail("No test user ID provided"));
        }

        var userId = userIdValues.FirstOrDefault() ?? "test-user";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
            new("sub", userId)
        };

        // Add role if specified
        if (Request.Headers.TryGetValue("X-Test-User-Role", out var roleValues))
        {
            var role = roleValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(claims, "TestScheme");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "TestScheme");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
