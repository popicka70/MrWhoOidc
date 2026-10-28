using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// An exception message can carry internal details (a connection string, a file path, a SQL fragment). The
/// endpoints that catch an exception log it and answer with a generic detail, like the global ProblemDetails
/// policy does for every other 5xx.
/// </summary>
[TestClass]
public sealed class ErrorDisclosureTests
{
    private const string Secret = "postgres://oidc:sup3rs3cret@db/authdb";

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
                new Claim(ClaimTypes.Name, "admin@example.com"),
                new Claim(ClaimTypes.Role, "platform-admin")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    /// <summary>An export service that always fails with a message that must not reach the caller.</summary>
    private sealed class ThrowingExportService : IConfigurationExportService
    {
        public Task<ExportManifest> ExportTenantAsync(Guid tenantId, ExportOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Secret);

        public Task<ExportManifest> ExportRealmAsync(Guid realmId, ExportOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Secret);

        public Task<ExportManifest> ExportClientAsync(Guid clientId, ExportOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Secret);

        public Task<ExportManifest> ExportIdentityProviderAsync(Guid providerId, ExportOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Secret);
    }

    [TestMethod]
    public async Task TheExportEndpoint_DoesNotDiscloseTheExceptionMessage()
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
                        options.AddPolicy("platform-admin", policy => policy.RequireAssertion(_ => true)));
                    services.AddSingleton<IConfigurationExportService, ThrowingExportService>();
                });
            });

        var client = factory.CreateSecureClient();

        string slug;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            slug = await db.Tenants.IgnoreQueryFilters().Select(t => t.Slug).FirstAsync();
        }

        using var response = await client.GetAsync($"/admin/api/platform/tenants/{slug}/export?mode=obfuscated");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.IsFalse(body.Contains(Secret, StringComparison.Ordinal),
            "the exception message must not reach the caller; it belongs in the log.");
        Assert.IsFalse(body.Contains("sup3rs3cret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TheLoginPage_DoesNotLogPersonalData()
    {
        // The username, the email address and the return URL are personal data; the return URL can even carry an
        // authorization code. The log carries their hash (or the path) instead of the value.
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "MrWhoOidc.WebAuth", "Pages", "Login.cshtml.cs"));

        foreach (var forbidden in new[] { "{Username}", "{Email}", "{User}", "{ReturnUrl}" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal),
                $"The login page must not log the personal data in {forbidden}; use LogTokenization.HashId.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MrWhoOidc.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the repository root (MrWhoOidc.slnx).");
        return directory!.FullName;
    }
}
