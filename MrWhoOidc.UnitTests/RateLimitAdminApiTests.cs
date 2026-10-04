using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RateLimitAdminApiTests
{
    private sealed class TestAuthHandler(
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
    public async Task RateLimitInspectionEndpoints_Return501_InsteadOfPlaceholderData()
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
                });
            });
        var client = factory.CreateClient();

        foreach (var path in new[] { "/admin/api/rate-limits/overview", "/admin/api/rate-limits/events", "/admin/api/rate-limits/client/some-client" })
        {
            var response = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.NotImplemented, response.StatusCode, path);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            StringAssert.Contains(problem.GetProperty("detail").GetString(), "not implemented");
        }

        var metrics = await client.GetAsync("/admin/api/rate-limits/metrics");
        Assert.AreEqual(HttpStatusCode.NotFound, metrics.StatusCode, "The placeholder metrics endpoint was removed.");
    }
}
