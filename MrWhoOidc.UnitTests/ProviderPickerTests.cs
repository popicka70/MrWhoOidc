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
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;

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

    [TestMethod]
    public void BuildTenantAwareUrl_Preserves_Explicit_Tenant_Prefix_When_Accessor_Is_SingleTenant()
    {
        using var db = CreateDb(nameof(BuildTenantAwareUrl_Preserves_Explicit_Tenant_Prefix_When_Accessor_Is_SingleTenant));
        var (model, http) = CreateModel(db);
        http.Request.Path = "/t/default/auth/providers/select";

        var url = model.BuildTenantAwareUrl("/Auth/External/Start");

        Assert.AreEqual("/t/default/Auth/External/Start", url);
    }

    [TestMethod]
    public async Task ProviderSelection_Returns_Only_ClientTenant_Providers_When_Names_Overlap()
    {
        using var db = CreateDb(nameof(ProviderSelection_Returns_Only_ClientTenant_Providers_When_Names_Overlap));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var client = new ClientEntity
        {
            TenantId = tenantA,
            ClientId = "tenant-client",
            ClientName = "Tenant Client",
            RealmId = Guid.NewGuid(),
            AllowLocalLogin = false,
            AllowExternalIdp = true
        };
        var tenantAProvider = new IdentityProvider { TenantId = tenantA, Name = "entra", DisplayName = "Tenant A Entra" };
        var tenantBProvider = new IdentityProvider { TenantId = tenantB, Name = "entra", DisplayName = "Tenant B Entra" };

        db.Clients.Add(client);
        db.IdentityProviders.AddRange(tenantAProvider, tenantBProvider);
        db.ClientIdentityProviders.AddRange(
            new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = tenantAProvider.Id, Enabled = true, Order = 1 },
            new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = tenantBProvider.Id, Enabled = true, Order = 2 });
        await db.SaveChangesAsync();

        var service = new ProviderSelectionService(db, new StaticClientStore(client));

        var result = await service.EvaluateAsync(client.ClientId, tenantId: tenantA);

        Assert.IsTrue(result.RequiresSelection);
        var providers = result.AvailableProviders?.ToList() ?? new List<ProviderOption>();
        Assert.HasCount(1, providers);
        Assert.AreEqual("Tenant A Entra", providers[0].DisplayName);
    }

    [TestMethod]
    public async Task ProviderSelection_Does_Not_AutoRedirect_To_CrossTenant_Mapping()
    {
        using var db = CreateDb(nameof(ProviderSelection_Does_Not_AutoRedirect_To_CrossTenant_Mapping));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var client = new ClientEntity
        {
            TenantId = tenantA,
            ClientId = "tenant-client-cross",
            ClientName = "Tenant Client Cross",
            RealmId = Guid.NewGuid(),
            AllowLocalLogin = false,
            AllowExternalIdp = true
        };
        var otherTenantProvider = new IdentityProvider { TenantId = tenantB, Name = "entra", DisplayName = "Other Tenant Entra" };

        db.Clients.Add(client);
        db.IdentityProviders.Add(otherTenantProvider);
        db.ClientIdentityProviders.Add(new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = otherTenantProvider.Id, Enabled = true, Order = 1 });
        await db.SaveChangesAsync();

        var service = new ProviderSelectionService(db, new StaticClientStore(client));

        var result = await service.EvaluateAsync(client.ClientId, idpParam: "entra", tenantId: tenantA);

        Assert.IsNull(result.AutoRedirectProvider);
        Assert.IsFalse(result.RequiresSelection);
        Assert.IsFalse(result.AvailableProviders?.Any() ?? false);
    }

    private sealed class StaticClientStore(ClientEntity client) : IClientStore
    {
        public Task<ClientEntity?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
            => Task.FromResult(string.Equals(client.ClientId, clientId, StringComparison.Ordinal) ? client : null);

        public Task<bool> ValidateClientSecretAsync(string clientId, string? clientSecret, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IQueryable<ClientEntity> QueryClients(CancellationToken ct = default)
            => new[] { client }.AsQueryable();

        public Task InvalidateClientCacheAsync(string clientId, Guid tenantId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ClientSecret?> GetPrimarySecretAsync(Guid clientRecordId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<ClientSecret>> GetActiveSecretsAsync(Guid clientRecordId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ClientSecret> CreateSecretAsync(Guid clientRecordId, string secretValue, string? description, string? createdBy, DateTime? expiresAtUtc = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ActivateSecretAsync(Guid secretId, string activatedBy, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetPrimarySecretAsync(Guid secretId, string updatedBy, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RevokeSecretAsync(Guid secretId, string revokedBy, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RecordSecretUsageAsync(Guid secretId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
