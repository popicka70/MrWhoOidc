using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using ProviderMappingsIndexModel = MrWhoOidc.WebAuth.Pages.Admin.ProviderMappings.IndexModel;
using RegistrationsIndexModel = MrWhoOidc.WebAuth.Pages.Admin.Registrations.IndexModel;
using RolesIndexPageModel = MrWhoOidc.WebAuth.Pages.Admin.Roles.IndexModel;
using PersistedClient = MrWhoOidc.Auth.Persistence.Client;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class AdminTenantIsolationTests
{
    private sealed class StubTenantAccessor : ITenantAccessor
    {
        public StubTenantAccessor(Guid tenantId, string slug, string name)
        {
            CurrentTenant = new TenantContext
            {
                TenantId = tenantId,
                Slug = slug,
                Name = name,
                IssuerUri = $"https://issuer/{slug}",
                IsMultiTenantMode = true
            };
        }

        public TenantContext? CurrentTenant { get; set; }

        public void SetTenant(TenantContext context)
        {
            CurrentTenant = context;
        }
    }

    private static MultiTenancyOptions DisabledMultiTenancy() => new()
    {
        Enabled = false,
        DefaultTenantSlug = "default"
    };

    private static AuthDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase("admin-tenant-isolation-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new AuthDbContext(options);
    }

    [TestMethod]
    public async Task RolesIndexModel_FiltersToCurrentTenant()
    {
        using var db = CreateDbContext();
        var tenantOne = new Tenant { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha", IssuerUri = "https://issuer/alpha", Status = TenantStatus.Active };
        var tenantTwo = new Tenant { Id = Guid.NewGuid(), Name = "Beta", Slug = "beta", IssuerUri = "https://issuer/beta", Status = TenantStatus.Active };
        db.Tenants.AddRange(tenantOne, tenantTwo);

        var realmOne = new Realm { Id = Guid.NewGuid(), Name = "alpha-realm", TenantId = tenantOne.Id };
        var realmTwo = new Realm { Id = Guid.NewGuid(), Name = "beta-realm", TenantId = tenantTwo.Id };
        db.Realms.AddRange(realmOne, realmTwo);

        db.Roles.AddRange(
            new Role { Id = Guid.NewGuid(), TenantId = tenantOne.Id, RealmId = realmOne.Id, Name = "alpha-role" },
            new Role { Id = Guid.NewGuid(), TenantId = tenantTwo.Id, RealmId = realmTwo.Id, Name = "beta-role" });
        await db.SaveChangesAsync();

        var tenantAccessor = new StubTenantAccessor(tenantOne.Id, tenantOne.Slug, tenantOne.Name);
        var model = new RolesIndexPageModel(db, tenantAccessor, DisabledMultiTenancy());

        await model.OnGetAsync();

        Assert.HasCount(1, model.Roles, "Only roles for the current tenant should be returned.");
        Assert.IsTrue(model.Roles.All(r => r.TenantId == tenantOne.Id), "Returned roles must belong to the current tenant.");
        Assert.IsTrue(model.Realms.All(r => r.TenantId == tenantOne.Id), "Realm filters should be scoped to the tenant context.");
    }

    [TestMethod]
    public async Task RegistrationsApproveAsync_DeniesCrossTenantRecords()
    {
        using var db = CreateDbContext();
        var tenantOne = new Tenant { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha", IssuerUri = "https://issuer/alpha", Status = TenantStatus.Active };
        var tenantTwo = new Tenant { Id = Guid.NewGuid(), Name = "Beta", Slug = "beta", IssuerUri = "https://issuer/beta", Status = TenantStatus.Active };
        db.Tenants.AddRange(tenantOne, tenantTwo);
        await db.SaveChangesAsync();

        var otherTenantRegistration = new Registration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantTwo.Id,
            Email = "user@example.com",
            NormalizedEmail = "USER@EXAMPLE.COM",
            State = "pending"
        };
        db.Set<Registration>().Add(otherTenantRegistration);
        await db.SaveChangesAsync();

        var tenantAccessor = new StubTenantAccessor(tenantOne.Id, tenantOne.Slug, tenantOne.Name);
        var model = new RegistrationsIndexModel(db, tenantAccessor, DisabledMultiTenancy());

        var result = await model.OnPostApproveAsync(otherTenantRegistration.Id);

        var redirect = result as RedirectResult;
        Assert.IsNotNull(redirect, "Cross-tenant approvals should redirect without processing.");
        Assert.IsTrue(string.Equals("/Admin/Registrations", redirect.Url, StringComparison.OrdinalIgnoreCase));

        var reloaded = await db.Set<Registration>().FindAsync(otherTenantRegistration.Id);
        Assert.AreEqual("pending", reloaded!.State, "Cross-tenant registration state must remain unchanged.");
    }

    [TestMethod]
    public async Task ProviderMappings_OnPost_BlocksCrossTenantClients()
    {
        using var db = CreateDbContext();
        var tenantOne = new Tenant { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha", IssuerUri = "https://issuer/alpha", Status = TenantStatus.Active };
        var tenantTwo = new Tenant { Id = Guid.NewGuid(), Name = "Beta", Slug = "beta", IssuerUri = "https://issuer/beta", Status = TenantStatus.Active };
        db.Tenants.AddRange(tenantOne, tenantTwo);

        var realmOne = new Realm { Id = Guid.NewGuid(), Name = "alpha-realm", TenantId = tenantOne.Id };
        var realmTwo = new Realm { Id = Guid.NewGuid(), Name = "beta-realm", TenantId = tenantTwo.Id };
        db.Realms.AddRange(realmOne, realmTwo);

        var tenantOneClient = new PersistedClient { Id = Guid.NewGuid(), TenantId = tenantOne.Id, RealmId = realmOne.Id, ClientId = "alpha-client" };
        var tenantTwoClient = new PersistedClient { Id = Guid.NewGuid(), TenantId = tenantTwo.Id, RealmId = realmTwo.Id, ClientId = "beta-client" };
        db.Clients.AddRange(tenantOneClient, tenantTwoClient);

        var tenantOneProvider = new IdentityProvider { Id = Guid.NewGuid(), TenantId = tenantOne.Id, Name = "alpha-idp" };
        db.IdentityProviders.Add(tenantOneProvider);
        await db.SaveChangesAsync();

        var tenantAccessor = new StubTenantAccessor(tenantOne.Id, tenantOne.Slug, tenantOne.Name);
        var model = new ProviderMappingsIndexModel(db, tenantAccessor, DisabledMultiTenancy())
        {
            Input = new ProviderMappingsIndexModel.InputModel
            {
                ClientId = tenantTwoClient.Id,
                IdentityProviderId = tenantOneProvider.Id,
                Enabled = true,
                IsDefaultForClient = false,
                AutoRedirectIfSingle = false,
                Order = 0
            }
        };

        var result = await model.OnPostAsync();

        Assert.IsInstanceOfType(result, typeof(PageResult), "Invalid tenant combinations should keep the user on the page.");
        Assert.IsTrue(model.ModelState.ContainsKey("Input.ClientId"), "ModelState should contain an error for the client field.");
        Assert.AreEqual(0, db.ClientIdentityProviders.Count(), "No mappings should be created for cross-tenant combinations.");
    }
}
