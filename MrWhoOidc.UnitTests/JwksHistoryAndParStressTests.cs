using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class JwksHistoryAndParStressTests
{
    [TestMethod]
    public async Task Record_Jwks_History_For_Client()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var realm = new Realm { Name = "r", DisplayName = "R" };
        db.Realms.Add(realm);
        var client = new Client { ClientId = "cli", RealmId = realm.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.ClientJwksHistories.Add(new ClientJwksHistory { ClientId = client.Id, JwksJson = "{\"keys\":[]}", Source = "manual" });
        db.ClientJwksHistories.Add(new ClientJwksHistory { ClientId = client.Id, JwksJson = "{\"keys\":[{\"kid\":\"1\"}]}", Source = "fetch" });
        await db.SaveChangesAsync();

        var list = await db.ClientJwksHistories.AsNoTracking().Where(h => h.ClientId == client.Id).OrderBy(h => h.CreatedAt).ToListAsync();
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual("manual", list[0].Source);
        Assert.AreEqual("fetch", list[1].Source);
    }

    [TestMethod]
    public void Par_Stress_Pending_Limit()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var options = Options.Create(new AuthOptions { ParClientPendingLimit = 50 });
        var store = new EfPushedAuthorizationRequestStore(db, options);

        var req = new MrWhoOidc.Auth.Protocols.AuthorizeRequest { response_type = "code", client_id = "cli", redirect_uri = "https://cb", scope = "openid" };

        var ids = new List<string>();
        for (int i = 0; i < 50; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            ids.Add(id);
            store.Create(id, req, "cli", TimeSpan.FromMinutes(5), null);
        }
        // Next create must fail due to limit
        try { store.Create(Guid.NewGuid().ToString("N"), req, "cli", TimeSpan.FromMinutes(5), null); Assert.Fail("Expected limit"); } catch (InvalidOperationException) { }

        // Consume half and ensure we can add the same number again
        for (int i = 0; i < 25; i++) { store.MarkConsumedById(ids[i]); }
        for (int i = 0; i < 25; i++) { store.Create(Guid.NewGuid().ToString("N"), req, "cli", TimeSpan.FromMinutes(5), null); }

        // Still at limit
        try { store.Create(Guid.NewGuid().ToString("N"), req, "cli", TimeSpan.FromMinutes(5), null); Assert.Fail("Expected limit"); } catch (InvalidOperationException) { }
    }
}
