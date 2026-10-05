using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// Assessment 2026-10-04 #3 / R7: clients are no longer created with every grant enabled and an empty
/// (= unrestricted) scope set. New clients default to no extra grants; creation paths derive the grant flags from
/// the client's grant types and assign scopes explicitly through <see cref="ClientProvisioning"/>.
/// </summary>
[TestClass]
public sealed class ClientProvisioningDefaultsTests
{
    [TestMethod]
    public void NewClient_HasNoExtraGrantsEnabled()
    {
        var client = new MrWhoOidc.Auth.Persistence.Client();

        Assert.IsFalse(client.AllowClientCredentials);
        Assert.IsFalse(client.AllowDeviceAuthorization);
        Assert.IsFalse(client.AllowCiba);
    }

    [TestMethod]
    public void GrantFlagColumns_DefaultToFalse_ForNewRows()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var entity = db.Model.FindEntityType(typeof(MrWhoOidc.Auth.Persistence.Client))!;

        Assert.AreEqual(false, entity.FindProperty(nameof(MrWhoOidc.Auth.Persistence.Client.AllowClientCredentials))!.GetDefaultValue());
        Assert.AreEqual(false, entity.FindProperty(nameof(MrWhoOidc.Auth.Persistence.Client.AllowDeviceAuthorization))!.GetDefaultValue());
        Assert.AreEqual(false, entity.FindProperty(nameof(MrWhoOidc.Auth.Persistence.Client.AllowCiba))!.GetDefaultValue());
    }

    [TestMethod]
    public void ApplyGrantTypes_DerivesFlags_AndDefaultsToAuthorizationCode()
    {
        var m2m = new MrWhoOidc.Auth.Persistence.Client();
        ClientProvisioning.ApplyGrantTypes(m2m, [OAuthConstants.GrantTypes.ClientCredentials, OAuthConstants.GrantTypes.Ciba]);
        Assert.IsTrue(m2m.AllowClientCredentials);
        Assert.IsTrue(m2m.AllowCiba);
        Assert.IsFalse(m2m.AllowDeviceAuthorization);

        var defaulted = new MrWhoOidc.Auth.Persistence.Client { AllowClientCredentials = true };
        ClientProvisioning.ApplyGrantTypes(defaulted, []);
        CollectionAssert.AreEqual(new[] { "authorization_code", "refresh_token" }, ClientProvisioning.GetEffectiveGrantTypes(defaulted).ToArray());
        Assert.IsFalse(defaulted.AllowClientCredentials);
    }

    [TestMethod]
    public void EffectiveGrantTypes_ForUnregisteredClient_AreAuthorizationCodeAndRefresh()
    {
        CollectionAssert.AreEqual(
            new[] { "authorization_code", "refresh_token" },
            ClientProvisioning.GetEffectiveGrantTypes(new MrWhoOidc.Auth.Persistence.Client { GrantTypesJson = null }).ToArray());
        Assert.AreEqual(0, ClientProvisioning.GetEffectiveGrantTypes(new MrWhoOidc.Auth.Persistence.Client { GrantTypesJson = "not-json" }).Count);
    }

    [TestMethod]
    public async Task AssignScopes_DefaultsToStandardScopes_CreatingThemWhenMissing()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var client = new MrWhoOidc.Auth.Persistence.Client { TenantId = Guid.NewGuid(), ClientId = "c" };
        db.Clients.Add(client);

        var assigned = await ClientProvisioning.AssignScopesAsync(db, client, null);
        await db.SaveChangesAsync();

        CollectionAssert.AreEqual(new[] { "openid", "profile", "email", "offline_access" }, assigned.ToArray());
        Assert.AreEqual(4, await db.ClientScopes.CountAsync(cs => cs.ClientId == client.Id));
        Assert.AreEqual(4, await db.Scopes.CountAsync(s => s.TenantId == null && s.IsGlobal));
    }

    [TestMethod]
    public async Task AssignScopes_SkipsUnknownAndOtherTenantScopes_AndIsIdempotent()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        db.Scopes.AddRange(
            new Scope { Name = "own.scope", TenantId = tenantId },
            new Scope { Name = "foreign.scope", TenantId = Guid.NewGuid() });
        var client = new MrWhoOidc.Auth.Persistence.Client { TenantId = tenantId, ClientId = "c" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        await ClientProvisioning.AssignScopesAsync(db, client, ["openid", "own.scope", "foreign.scope", "nope"]);
        await db.SaveChangesAsync();
        await ClientProvisioning.AssignScopesAsync(db, client, ["openid", "own.scope"]);
        await db.SaveChangesAsync();

        var names = await db.ClientScopes.Where(cs => cs.ClientId == client.Id).Select(cs => cs.ScopeName).OrderBy(s => s).ToArrayAsync();
        CollectionAssert.AreEqual(new[] { "openid", "own.scope" }, names);
    }
}
