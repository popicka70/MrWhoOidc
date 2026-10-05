using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Models.DynamicRegistration;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Assessment 2026-10-04, R7 / #3 at the dynamic registration endpoint: a DCR client gets exactly the scopes
/// it registered for (never the protected ones) and grant flags that follow its grant_types.
/// </summary>
public sealed partial class DynamicClientRegistrationTests
{
    private static async Task<(ClientRegistrationResponse Response, MrWhoOidc.Auth.Persistence.Client Stored, string[] Scopes, AuthDbContext Db)> RegisterAsync(
        ClientRegistrationRequest request)
    {
        var db = CreateDb();
        var tenantId = await CreateTestTenant(db);
        db.Scopes.Add(new Scope { Name = "api.read", IsGlobal = true, IsExposed = true });
        db.Scopes.Add(new Scope { Name = "tenants", IsGlobal = true, IsExposed = true });
        db.Scopes.Add(new Scope { Name = "other-tenant.scope", TenantId = Guid.NewGuid(), IsExposed = true });
        await db.SaveChangesAsync();

        var tenantAccessor = new MrWhoOidc.Auth.MultiTenancy.TenantAccessor();
        SetTenant(tenantAccessor, tenantId);
        var handler = new RegistrationHandler(
            db,
            tenantAccessor,
            Options.Create(new AuthOptions { EnableDynamicClientRegistration = true }),
            new TestPlatformSettingsService(dynamicClientRegistrationEnabled: true),
            new TestPlatformInitialAccessTokenService(validTokens: new[] { DefaultValidInitialAccessToken }),
            new TestPasswordHasher(),
            new NoopHttpClientFactory(),
            NullLogger<RegistrationHandler>.Instance);

        var ctx = CreateHttpContext(body: JsonSerializer.Serialize(request));
        var result = await handler.HandleAsync(ctx);
        await result.ExecuteAsync(ctx);
        Assert.AreEqual(201, ctx.Response.StatusCode);

        var response = (await GetResponseBody<ClientRegistrationResponse>(ctx))!;
        var stored = await db.Clients.SingleAsync(c => c.ClientId == response.ClientId);
        var scopes = await db.ClientScopes.Where(cs => cs.ClientId == stored.Id).Select(cs => cs.ScopeName).OrderBy(s => s).ToArrayAsync();
        return (response, stored, scopes, db);
    }

    [TestMethod]
    public async Task Register_WithScope_AssignsOnlyKnownUnprotectedScopes_AndEchoesThem()
    {
        var (response, _, scopes, _) = await RegisterAsync(new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            Scope = "openid profile api.read tenants mrwho:admin other-tenant.scope not-a-scope"
        });

        CollectionAssert.AreEqual(new[] { "api.read", "openid", "profile" }, scopes);
        Assert.AreEqual("openid profile api.read", response.Scope);
    }

    [TestMethod]
    public async Task Register_WithoutScope_AssignsTheDefaultScopes()
    {
        var (_, _, scopes, _) = await RegisterAsync(new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"]
        });

        CollectionAssert.AreEqual(new[] { "email", "offline_access", "openid", "profile" }, scopes);
    }

    [TestMethod]
    public async Task Register_GrantFlags_FollowRegisteredGrantTypes()
    {
        var (_, interactive, _, _) = await RegisterAsync(new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            GrantTypes = ["authorization_code", "refresh_token"]
        });
        Assert.IsFalse(interactive.AllowClientCredentials);
        Assert.IsFalse(interactive.AllowDeviceAuthorization);
        Assert.IsFalse(interactive.AllowCiba);

        var (_, m2m, _, _) = await RegisterAsync(new ClientRegistrationRequest
        {
            RedirectUris = ["https://client.example.com/callback"],
            GrantTypes = ["client_credentials"]
        });
        Assert.IsTrue(m2m.AllowClientCredentials);
        Assert.IsFalse(m2m.AllowDeviceAuthorization);
        Assert.IsFalse(m2m.AllowCiba);
    }
}
