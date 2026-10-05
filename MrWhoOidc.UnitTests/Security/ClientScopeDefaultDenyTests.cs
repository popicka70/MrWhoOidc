using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Assessment 2026-10-04 R7: a client with no ClientScopes rows could request ANY scope. Scope assignment is now
/// default-deny everywhere (authorize, claims-implied scopes, device/CIBA issuance): only 'openid' is allowed
/// when nothing is assigned.
/// </summary>
[TestClass]
public sealed class ClientScopeDefaultDenyTests
{
    private static async Task<AuthorizeValidationResult> AuthorizeAsync(string scope, string? claims = null, params string[] clientScopes)
    {
        var db = TestDataSeeder.CreateInMemoryDb();
        var client = new MrWhoOidc.Auth.Persistence.Client
        {
            ClientId = "rp",
            TokenEndpointAuthMethod = "client_secret_basic",
            AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(new[] { "https://app/callback" }),
        };
        db.Clients.Add(client);
        foreach (var s in clientScopes)
        {
            db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = s });
        }
        await db.SaveChangesAsync();

        var clients = new Mock<IClientStore>();
        clients.Setup(c => c.FindByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(client);
        clients.Setup(c => c.GetActiveSecretsAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ClientSecret>());

        var validator = new AuthorizeRequestValidator(db, clients.Object, NullLogger<AuthorizeRequestValidator>.Instance);
        return await validator.ValidateAsync(new AuthorizeRequest(
            response_type: "code",
            client_id: "rp",
            redirect_uri: "https://app/callback",
            scope: scope,
            nonce: "n",
            code_challenge: "aaabbbcccdddeeefffaaabbbcccdddeeefffaaabbbcc",
            code_challenge_method: "S256",
            claims: claims));
    }

    [TestMethod]
    public async Task Authorize_ClientWithoutAssignedScopes_MayNotRequestOtherScopes()
    {
        var result = await AuthorizeAsync("openid profile api.write");

        Assert.IsFalse(result.IsValid, "a client with no scope assignments must not obtain arbitrary scopes");
        Assert.AreEqual("invalid_scope", result.Error);
        StringAssert.Contains(result.ErrorDescription, "profile");
        StringAssert.Contains(result.ErrorDescription, "api.write");
    }

    [TestMethod]
    public async Task Authorize_ClientWithoutAssignedScopes_MayStillRequestOpenid()
    {
        var result = await AuthorizeAsync("openid");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
    }

    [TestMethod]
    public async Task Authorize_AssignedScopes_AreAllowed_OthersRejected()
    {
        Assert.IsTrue((await AuthorizeAsync("openid profile", null, "openid", "profile")).IsValid);
        Assert.IsFalse((await AuthorizeAsync("openid profile email", null, "openid", "profile")).IsValid);
    }

    [TestMethod]
    public async Task Authorize_ClaimsParameter_DoesNotImplyScopes_ForClientWithoutAssignments()
    {
        var result = await AuthorizeAsync("openid", """{"id_token":{"email":null,"name":null}}""");

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        CollectionAssert.AreEqual(new[] { "openid" }, result.Scopes.ToArray());
    }

    private static async Task<(IReadOnlyDictionary<string, object> Response, string? ScopeClaim)> IssueDeviceTokenAsync(string[] requested, params string[] clientScopes)
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var realm = new Realm { TenantId = tenantId, Name = "default" };
        var client = new MrWhoOidc.Auth.Persistence.Client { TenantId = tenantId, ClientId = "tv", RealmId = realm.Id, AllowDeviceAuthorization = true };
        var user = new User { TenantId = tenantId, Username = "u", Email = "u@example.com", Name = "U" };
        db.AddRange(realm, client, user);
        foreach (var s in clientScopes)
        {
            db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = s });
        }
        await db.SaveChangesAsync();

        List<Claim>? issued = null;
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.CreateJwtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, IEnumerable<Claim> claims, DateTimeOffset _, string? _, string? _, DateTimeOffset? _, string? _, CancellationToken _) => issued = claims.ToList())
            .ReturnsAsync("jwt");

        var factory = new DeviceCodeTokenFactory(db, jwt.Object, new MockTenantSettingsService(), new MockScopeResolver(), new TokenLifetimeResolver(), PublicSubjects());
        var (ok, payload, error, _) = await factory.CreateTokenAsync(new DeviceCodeTokenRequest("tv", user.Id, requested, "api", "https://idp/t/x"));
        Assert.IsTrue(ok, error);
        var response = (IReadOnlyDictionary<string, object>)payload!;
        return (response, issued!.SingleOrDefault(c => c.Type == "scope")?.Value);
    }

    [TestMethod]
    public async Task DeviceToken_ClientWithoutAssignedScopes_GetsOnlyOpenid_AndNoRefreshToken()
    {
        var (response, scope) = await IssueDeviceTokenAsync(["openid", "profile", "email", "offline_access"]);

        Assert.AreEqual("openid", scope);
        Assert.IsFalse(response.ContainsKey("refresh_token"), "offline_access is not assigned, so no refresh token");
    }

    [TestMethod]
    public async Task DeviceToken_AssignedScopes_AreGranted_IncludingOfflineAccess()
    {
        var (response, scope) = await IssueDeviceTokenAsync(["openid", "profile", "offline_access"], "openid", "profile", "offline_access");

        Assert.AreEqual("openid profile offline_access", scope);
        Assert.IsTrue(response.ContainsKey("refresh_token"));
    }

    private static MrWhoOidc.Auth.Services.SubjectIdentifiers.IPairwiseSubjectService PublicSubjects()
    {
        var subjects = new Mock<MrWhoOidc.Auth.Services.SubjectIdentifiers.IPairwiseSubjectService>();
        subjects.Setup(s => s.GetSubjectAsync(It.IsAny<MrWhoOidc.Auth.Persistence.Client>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MrWhoOidc.Auth.Persistence.Client _, Guid userId, CancellationToken _) => userId.ToString());
        return subjects.Object;
    }
}
