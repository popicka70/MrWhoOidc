using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass, TestCategory("RequiresPostgres")]
public sealed class ParStoreTests
{
    [TestMethod]
    public void EfParStore_Create_Get_Consume_WithPendingLimit()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        // Set a low pending limit to exercise the limit path
        var options = Options.Create(new AuthOptions { ParClientPendingLimit = 2 });
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var store = new EfPushedAuthorizationRequestStore(db, options, tenantAccessor);

        var req = new AuthorizeRequest
        (
            response_type: "code",
            client_id: "spa",
            redirect_uri: "https://app.example.com/callback",
            scope: "openid"
        );
        var id1 = Guid.NewGuid().ToString("N");
        var id2 = Guid.NewGuid().ToString("N");
        var id3 = Guid.NewGuid().ToString("N");

        // Create two entries (under the limit)
        store.Create(id1, req, "spa", TimeSpan.FromMinutes(5), null);
        store.Create(id2, req, "spa", TimeSpan.FromMinutes(5), null);

        // Third should throw due to pending limit
        try
        {
            store.Create(id3, req, "spa", TimeSpan.FromMinutes(5), null);
            Assert.Fail("Expected InvalidOperationException due to pending limit");
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        // Get by id (non-consuming)
        var e1 = store.TryGetById(id1);
        Assert.IsNotNull(e1);
        Assert.AreEqual("spa", e1!.ClientId);

        // Consume id1
        var consumed = store.TryConsumeById(id1);
        Assert.IsNotNull(consumed);
        Assert.IsNull(store.TryGetById(id1));

        // Now we can add another under the limit
        store.Create(id3, req, "spa", TimeSpan.FromMinutes(5), null);
        Assert.IsNotNull(store.TryGetById(id3));
    }

    [TestMethod]
    public void EfParStore_Expiry_Cleans_Up()
    {
        using var db = TestDataSeeder.CreateInMemoryDb();
        var options = Options.Create(new AuthOptions { ParClientPendingLimit = 10 });
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var store = new EfPushedAuthorizationRequestStore(db, options, tenantAccessor);

        var req = new AuthorizeRequest(response_type: "code", client_id: "spa", redirect_uri: "https://app.example.com/callback", scope: "openid");
        var id = Guid.NewGuid().ToString("N");
        store.Create(id, req, "spa", TimeSpan.FromMilliseconds(10), null);
        System.Threading.Thread.Sleep(20);
        // Expired -> TryGetById returns null and opportunistically cleans up
        var e = store.TryGetById(id);
        Assert.IsNull(e);
        Assert.AreEqual(0, db.PushedAuthorizationRequests.Count());
    }
}
