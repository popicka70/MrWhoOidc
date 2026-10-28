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
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AdminInvitationsApiTests
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
                new Claim(ClaimTypes.Role, "tenant-admin")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    /// <summary>A stable user id: the antiforgery token is bound to it.</summary>
    private const string AdminTestUserId = "11111111-1111-1111-1111-111111111111";

    [TestMethod]
    public async Task Invitations_Create_List_And_Revoke_Work_Through_AdminApi()
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
                    services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MrWhoOidc.UnitTests.TestDoubles.AllowTenantAdminOperationsHandler>();
                });
            });

        var client = factory.CreateSecureClient();

        // M-CSRF: the admin API is antiforgery protected for cookie-authenticated clients; the bearer
        // authenticated CLI does not need the token.
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/api/invitations")
        {
            Content = JsonContent.Create(new
            {
                email = "cli-invite@example.com",
                displayName = "CLI Invitee",
                isTenantAdmin = true,
                validDays = 14
            })
        };
        TestAntiforgeryHelper.AddToken(client, createRequest, factory.Services, AdminTestUserId, "admin@example.com");
        var createResponse = await client.SendAsync(createRequest);

        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var invitation = created.GetProperty("invitation");
        var invitationId = invitation.GetProperty("id").GetGuid();
        Assert.AreEqual("cli-invite@example.com", invitation.GetProperty("email").GetString());
        Assert.AreEqual("Pending", invitation.GetProperty("status").GetString());
        Assert.IsTrue(created.GetProperty("invitationLink").GetString()?.Contains("/invitations/inv_") == true);

        var invitations = await client.GetFromJsonAsync<JsonElement[]>("/admin/api/invitations");
        Assert.IsNotNull(invitations);
        Assert.IsTrue(invitations.Any(item => item.GetProperty("id").GetGuid() == invitationId));

        var revokeRequest = new HttpRequestMessage(HttpMethod.Delete, $"/admin/api/invitations/{invitationId}?reason=unit-test");
        TestAntiforgeryHelper.AddToken(client, revokeRequest, factory.Services, AdminTestUserId, "admin@example.com");
        var revokeResponse = await client.SendAsync(revokeRequest);
        Assert.AreEqual(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        invitations = await client.GetFromJsonAsync<JsonElement[]>("/admin/api/invitations");
        Assert.IsNotNull(invitations);
        var revoked = invitations.Single(item => item.GetProperty("id").GetGuid() == invitationId);
        Assert.AreEqual("Revoked", revoked.GetProperty("status").GetString());
    }
}