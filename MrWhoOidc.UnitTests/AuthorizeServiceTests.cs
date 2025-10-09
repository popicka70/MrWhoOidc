using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizeServiceTests
{
    private static readonly Guid DefaultTenantId = new("00000000-0000-0000-0000-000000000001");

    private static AuthDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuthDbContext(opts);
    }

    [TestMethod]
    public async Task ValidateAsync_RequiresOpenIdScope_AndPkceWhenEnabled()
    {
        using var db = CreateDb();
        var client = new ClientEntity
        {
            ClientId = "spa",
            RequirePkce = true,
            RequireConsent = false,
            AllowedLoginRedirectUrisJson = "[\"https://app.example.com/callback\"]",
            TenantId = DefaultTenantId
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var svc = new AuthorizeService(db, new ClientStore(db, new NoopHasher(), tenantAccessor));
        var reqMissingPkce = new MrWhoOidc.Auth.Protocols.AuthorizeRequest
        {
            response_type = "code",
            client_id = "spa",
            redirect_uri = "https://app.example.com/callback",
            scope = "profile"
        };
        var res1 = await svc.ValidateAsync(reqMissingPkce);
        Assert.IsFalse(res1.IsValid);
        StringAssert.Contains(res1.ErrorDescription!, "PKCE");

        var reqNoOpenId = new MrWhoOidc.Auth.Protocols.AuthorizeRequest
        {
            response_type = "code",
            client_id = "spa",
            redirect_uri = "https://app.example.com/callback",
            code_challenge = new string('a', 43),
            code_challenge_method = "S256",
            scope = "profile email",
            nonce = "n"
        };
        var res2 = await svc.ValidateAsync(reqNoOpenId);
        Assert.IsFalse(res2.IsValid);
        Assert.AreEqual("invalid_scope", res2.Error);
    }

    [TestMethod]
    public async Task ValidateAsync_Succeeds_ForMinimalValidRequest()
    {
        using var db = CreateDb();
        var client = new ClientEntity
        {
            ClientId = "spa",
            RequirePkce = true,
            RequireConsent = true,
            AllowedLoginRedirectUrisJson = "[\"https://app.example.com/oidc-cb\"]",
            TenantId = DefaultTenantId
        };
        // Assign scopes so enforcement is active
        db.Clients.Add(client);
        db.Scopes.Add(new Scope { Name = "openid" });
        db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = "openid" });
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var svc = new AuthorizeService(db, new ClientStore(db, new NoopHasher(), tenantAccessor));
        var req = new MrWhoOidc.Auth.Protocols.AuthorizeRequest
        {
            response_type = "code",
            client_id = "spa",
            redirect_uri = "https://app.example.com/oidc-cb",
            scope = "openid",
            code_challenge = new string('x', 43),
            code_challenge_method = "S256",
            nonce = "n"
        };
        var res = await svc.ValidateAsync(req);
        Assert.IsTrue(res.IsValid);
        Assert.AreEqual("spa", res.ClientId);
        Assert.AreEqual("https://app.example.com/oidc-cb", res.RedirectUri);
        Assert.IsTrue(res.RequireConsent);
        CollectionAssert.Contains(res.Scopes, "openid");
    }

    [TestMethod]
    public async Task ValidateAsync_Fails_WhenRequestedScopesNotAllowed()
    {
        using var db = CreateDb();
        var client = new ClientEntity
        {
            ClientId = "spa",
            RequirePkce = true,
            RequireConsent = false,
            AllowedLoginRedirectUrisJson = "[\"https://app.example.com/callback\"]",
            TenantId = DefaultTenantId
        };
        db.Clients.Add(client);
        db.Scopes.Add(new Scope { Name = "openid" });
        await db.SaveChangesAsync();

        db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = "openid" });
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var svc = new AuthorizeService(db, new ClientStore(db, new NoopHasher(), tenantAccessor));
        var req = new MrWhoOidc.Auth.Protocols.AuthorizeRequest
        {
            response_type = "code",
            client_id = "spa",
            redirect_uri = "https://app.example.com/callback",
            scope = "openid profile",
            code_challenge = new string('p', 43),
            code_challenge_method = "S256",
            nonce = "n"
        };

        var res = await svc.ValidateAsync(req);

        Assert.IsFalse(res.IsValid);
        Assert.AreEqual("invalid_scope", res.Error);
        StringAssert.Contains(res.ErrorDescription!, "profile");
    }

    [TestMethod]
    public async Task ValidateAsync_Fails_WhenRedirectUriNotAllowListed()
    {
        using var db = CreateDb();
        var client = new ClientEntity
        {
            ClientId = "spa",
            RequirePkce = true,
            RequireConsent = false,
            AllowedLoginRedirectUrisJson = "[\"https://app.example.com/callback\"]",
            TenantId = DefaultTenantId
        };
        db.Clients.Add(client);
        db.Scopes.Add(new Scope { Name = "openid" });
        db.ClientScopes.Add(new ClientScope { ClientId = client.Id, ScopeName = "openid" });
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var svc = new AuthorizeService(db, new ClientStore(db, new NoopHasher(), tenantAccessor));
        var req = new MrWhoOidc.Auth.Protocols.AuthorizeRequest
        {
            response_type = "code",
            client_id = "spa",
            redirect_uri = "https://evil.example.com/callback",
            scope = "openid",
            code_challenge = new string('r', 43),
            code_challenge_method = "S256",
            nonce = "n"
        };

        var res = await svc.ValidateAsync(req);

        Assert.IsFalse(res.IsValid);
        Assert.AreEqual("invalid_request", res.Error);
        StringAssert.Contains(res.ErrorDescription!, "redirect_uri");
    }

    private sealed class NoopHasher : IPasswordHasher
    {
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => password == hash;
    }
}
