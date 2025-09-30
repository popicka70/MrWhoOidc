using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class IdpProviderMappingTests
{
    [TestMethod]
    public async Task ClientProvider_Mapping_Filters_Enabled_And_Order()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        // Realms and client
        var realm = new Realm { Name = "r", DisplayName = "R" };
        db.Realms.Add(realm);
    var client = new ClientEntity { ClientId = "cli", RealmId = realm.Id, AllowedLoginRedirectUrisJson = "[\"https://cb\"]" };
        db.Clients.Add(client);

        // Providers
        var p1 = new IdentityProvider { Name = "google", DisplayName = "Google", Enabled = true, SortOrder = 2 };
        var p2 = new IdentityProvider { Name = "aad", DisplayName = "AAD", Enabled = true, SortOrder = 1 };
        var p3 = new IdentityProvider { Name = "legacy", DisplayName = "Legacy", Enabled = false, SortOrder = 0 };
        db.IdentityProviders.AddRange(p1, p2, p3);
        await db.SaveChangesAsync();

        // Mapping
        db.ClientIdentityProviders.Add(new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = p1.Id, Enabled = true, Order = 2 });
        db.ClientIdentityProviders.Add(new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = p2.Id, Enabled = true, Order = 1 });
        db.ClientIdentityProviders.Add(new ClientIdentityProvider { ClientId = client.Id, IdentityProviderId = p3.Id, Enabled = true, Order = 3 }); // provider disabled globally
        await db.SaveChangesAsync();

        // Query like UI does (Select page): enabled mappings joined with enabled providers, ordered by mapping order
        var enabled = await db.ClientIdentityProviders.AsNoTracking()
            .Where(m => m.ClientId == client.Id && m.Enabled)
            .Join(db.IdentityProviders.AsNoTracking().Where(p => p.Enabled), m => m.IdentityProviderId, p => p.Id, (m, p) => new { m, p })
            .OrderBy(x => x.m.Order)
            .Select(x => new { name = x.p.Name, display = x.p.DisplayName })
            .ToListAsync();

        Assert.AreEqual(2, enabled.Count);
        Assert.AreEqual("aad", enabled[0].name);
        Assert.AreEqual("google", enabled[1].name);
    }
}
