using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// The admin endpoints must answer with a status, not with an unhandled exception: a missing client is a 404, and
/// an oversized upload is refused before it is buffered.
/// </summary>
[TestClass]
public sealed class AdminEndpointHardeningTests
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
                new Claim(ClaimTypes.NameIdentifier, AdminTestUserId),
                new Claim(ClaimTypes.Name, "admin@example.com"),
                new Claim(ClaimTypes.Role, "tenant-admin"),
                new Claim(ClaimTypes.Role, "platform-admin")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private const string AdminTestUserId = "11111111-1111-1111-1111-111111111111";

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
                        options.AddPolicy("platform-admin", policy => policy.RequireAssertion(_ => true));
                    });
                    services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MrWhoOidc.UnitTests.TestDoubles.AllowTenantAdminOperationsHandler>();
                });
            });

    [TestMethod]
    public async Task LinkingAProviderToAnUnknownClient_Answers404_Not500()
    {
        using var factory = CreateFactory();
        var client = factory.CreateSecureClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/api/clients/{Guid.NewGuid()}/providers")
        {
            Content = JsonContent.Create(new { identityProviderId = Guid.NewGuid() })
        };
        TestAntiforgeryHelper.AddToken(client, request, factory.Services, AdminTestUserId, "admin@example.com");

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode,
            "an unknown client is a 404; the endpoint must not dereference the missing client.");
    }

    [TestMethod]
    public async Task AnOversizedTenantIcon_IsRefusedWith413()
    {
        using var factory = CreateFactory();
        var client = factory.CreateSecureClient();

        // One byte more than the service accepts.
        var oversized = new byte[TenantIconService.MaxIconBytes + 1];
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(oversized);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "big.png");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/api/tenants/{Guid.NewGuid()}/icon")
        {
            Content = content
        };
        TestAntiforgeryHelper.AddToken(client, request, factory.Services, AdminTestUserId, "admin@example.com");

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode,
            "the size is checked before the file is copied into memory.");
    }
}
