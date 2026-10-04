using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Third 2026-10-04 review: claims requested through the `claims` parameter were released without their scope,
/// past the client's scope allow-list and the consent screen. /authorize now adds the covering scope (when the
/// client may request it), so consent sees it; issuance releases claims by scope only.
/// </summary>
[TestClass]
public sealed class ClaimsParameterScopeTests
{
    private static async Task<AuthorizeValidationResult> ValidateAsync(string claims, params string[] clientScopes)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "rp",
            TokenEndpointAuthMethod = "client_secret_basic",
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app/callback" }),
        };
        db.Clients.Add(client);
        foreach (var scope in clientScopes)
        {
            db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = scope });
        }
        await db.SaveChangesAsync();

        var clients = new Mock<IClientStore>();
        clients.Setup(c => c.FindByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(client);
        clients.Setup(c => c.GetActiveSecretsAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ClientSecret>());

        var validator = new AuthorizeRequestValidator(db, clients.Object, NullLogger<AuthorizeRequestValidator>.Instance);
        var result = await validator.ValidateAsync(new AuthorizeRequest(
            response_type: "code",
            client_id: "rp",
            redirect_uri: "https://app/callback",
            scope: "openid",
            nonce: "n",
            code_challenge: "aaabbbcccdddeeefffaaabbbcccdddeeefffaaabbbcc",
            code_challenge_method: "S256",
            claims: claims));
        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        return result;
    }

    [TestMethod]
    public async Task ClaimsRequest_AddsTheCoveringScopes_SoConsentSeesThem()
    {
        var result = await ValidateAsync("""{"id_token":{"email":null},"userinfo":{"name":{"essential":true},"roles":null}}""");

        CollectionAssert.IsSubsetOf(new[] { "openid", "email", "profile", "roles" }, result.Scopes);
    }

    [TestMethod]
    public async Task ClaimsRequest_DoesNotAddAScopeTheClientMayNotRequest()
    {
        var result = await ValidateAsync("""{"id_token":{"email":null,"name":null}}""", "openid", "profile");

        CollectionAssert.Contains(result.Scopes, "profile");
        CollectionAssert.DoesNotContain(result.Scopes, "email", "email is not assigned to this client, so the claim is not returned");
    }
}
