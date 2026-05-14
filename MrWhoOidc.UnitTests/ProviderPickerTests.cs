using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Auth.Providers;
using MrWhoOidc.UnitTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ProviderPickerTests
{
    private static AuthDbContext CreateDb(string name)
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AuthDbContext(opts);
    }

    private static string BuildLastProviderCookieName(string clientId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
        var bucket = Convert.ToHexString(bytes.AsSpan(0, 8));
        return ".mrwhooidc.lastidp." + bucket;
    }

    private static (SelectModel model, DefaultHttpContext http) CreateModel(AuthDbContext db, MockTenantAccessor? tenantAccessor = null)
    {
        var http = new DefaultHttpContext();
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var pageContext = new PageContext(actionContext);
        var model = new SelectModel(
            db,
            new StubLoginContinuationStore(),
            tenantAccessor ?? MockTenantAccessor.CreateSingleTenantMode(),
            NullLogger<SelectModel>.Instance)
        {
            PageContext = pageContext
        };
        return (model, http);
    }

    [TestMethod]
    public async Task Recommends_Provider_Based_On_LastProvider_Cookie()
    {
        using var db = CreateDb(nameof(Recommends_Provider_Based_On_LastProvider_Cookie));
        var realmId = Guid.NewGuid();
        var client = new ClientEntity { ClientId = "spaclient", ClientName = "SPA", RealmId = realmId, AllowLocalLogin = false };
        db.Clients.Add(client);
        var idpA = new IdentityProvider { Name = "google", DisplayName = "Google" };
        var idpB = new IdentityProvider { Name = "github", DisplayName = "GitHub" };
        db.IdentityProviders.AddRange(idpA, idpB);
        db.ClientIdentityProviders.AddRange(
            new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = idpA.Id, Order = 1 },
            new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = idpB.Id, Order = 2 }
        );
        await db.SaveChangesAsync();

        var (model, http) = CreateModel(db);
        model.Client_Id = client.ClientId;
        model.ReturnUrl = "/authorize?client_id=spaclient";

        var cookieName = BuildLastProviderCookieName(client.ClientId);
        http.Request.Headers.Append("Cookie", $"{cookieName}=github");

        var result = await model.OnGetAsync();
        Assert.IsInstanceOfType(result, typeof(PageResult));
        Assert.HasCount(2, model.Providers);
        Assert.AreEqual("github", model.Providers[0].Name, "Last provider should be first and recommended");
        Assert.IsTrue(model.Providers[0].IsRecommended, "Provider should be marked as recommended");
        Assert.AreEqual("last", model.RecommendationSource);
    }

    [TestMethod]
    public async Task Recommends_Provider_Based_On_Idp_Hint()
    {
        using var db = CreateDb(nameof(Recommends_Provider_Based_On_Idp_Hint));
        var realmId = Guid.NewGuid();
        var client = new ClientEntity { ClientId = "spaclient2", ClientName = "SPA 2", RealmId = realmId, AllowLocalLogin = true };
        db.Clients.Add(client);
        var idpA = new IdentityProvider { Name = "contoso", DisplayName = "Contoso" };
        var idpB = new IdentityProvider { Name = "fabrikam", DisplayName = "Fabrikam" };
        db.IdentityProviders.AddRange(idpA, idpB);
        db.ClientIdentityProviders.AddRange(
            new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = idpA.Id, Order = 1 },
            new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = idpB.Id, Order = 2 }
        );
        await db.SaveChangesAsync();

        var (model, http) = CreateModel(db);
        model.Client_Id = client.ClientId;
        model.ReturnUrl = "/authorize?client_id=spaclient2";
        model.Idp_Hint = "fabrikam";

        var result = await model.OnGetAsync();
        Assert.IsInstanceOfType(result, typeof(PageResult));
        Assert.HasCount(2, model.Providers);
        Assert.AreEqual("fabrikam", model.Providers[0].Name);
        Assert.IsTrue(model.Providers[0].IsRecommended);
        Assert.AreEqual("hint", model.RecommendationSource);
    }

    [TestMethod]
    public async Task Link_Mode_Shows_Only_Enabled_Oidc_Providers_For_Current_Tenant()
    {
        using var db = CreateDb(nameof(Link_Mode_Shows_Only_Enabled_Oidc_Providers_For_Current_Tenant));
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var currentTenantId = tenantAccessor.CurrentTenant!.TenantId;

        db.IdentityProviders.AddRange(
            new IdentityProvider
            {
                TenantId = currentTenantId,
                Name = "tenant-oidc",
                DisplayName = "Tenant OIDC",
                Type = IdentityProviderType.Oidc,
                Enabled = true,
                SortOrder = 0
            },
            new IdentityProvider
            {
                TenantId = currentTenantId,
                Name = "tenant-saml",
                DisplayName = "Tenant SAML",
                Type = IdentityProviderType.Saml,
                Enabled = true,
                SortOrder = 1
            },
            new IdentityProvider
            {
                TenantId = Guid.NewGuid(),
                Name = "other-tenant-oidc",
                DisplayName = "Other Tenant OIDC",
                Type = IdentityProviderType.Oidc,
                Enabled = true,
                SortOrder = 2
            },
            new IdentityProvider
            {
                TenantId = currentTenantId,
                Name = "disabled-oidc",
                DisplayName = "Disabled OIDC",
                Type = IdentityProviderType.Oidc,
                Enabled = false,
                SortOrder = 3
            });
        await db.SaveChangesAsync();

        var (model, _) = CreateModel(db, tenantAccessor);
        model.IsLinkMode = true;
        model.ReturnUrl = "/account/linked-accounts";

        var result = await model.OnGetAsync();

        Assert.IsInstanceOfType(result, typeof(PageResult));
        Assert.HasCount(1, model.Providers);
        Assert.AreEqual("tenant-oidc", model.Providers[0].Name);
    }
}
