using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ClientStoreTests
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
    public async Task ValidateClientSecret_PublicClient_AllowsNoSecret()
    {
        using var db = CreateDb();
        db.Clients.Add(new ClientEntity { ClientId = "public-app", RequirePkce = true, RequireConsent = false, TenantId = DefaultTenantId });
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var store = new ClientStore(db, new DummyHasher(), tenantAccessor);
        var ok = await store.ValidateClientSecretAsync("public-app", null);
        Assert.IsTrue(ok);

        var ok2 = await store.ValidateClientSecretAsync("public-app", "");
        Assert.IsTrue(ok2);
    }

    [TestMethod]
    public async Task ValidateClientSecret_ConfidentialClient_UsesHasher()
    {
        using var db = CreateDb();
        var hasher = new DummyHasher(correct: "top-secret");
        db.Clients.Add(new ClientEntity { ClientId = "conf-app", ClientSecretHash = hasher.Hash("top-secret"), TenantId = DefaultTenantId });
        await db.SaveChangesAsync();

        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var store = new ClientStore(db, hasher, tenantAccessor);
        Assert.IsTrue(await store.ValidateClientSecretAsync("conf-app", "top-secret"));
        Assert.IsFalse(await store.ValidateClientSecretAsync("conf-app", "wrong"));
        Assert.IsFalse(await store.ValidateClientSecretAsync("conf-app", null));
    }

    private sealed class DummyHasher : IPasswordHasher
    {
        private readonly string? _correct;
        public DummyHasher(string? correct = null) { _correct = correct; }
        public string Hash(string password) => password; // echo
        public bool Verify(string password, string hash) => (_correct ?? hash) == password;
    }
}
