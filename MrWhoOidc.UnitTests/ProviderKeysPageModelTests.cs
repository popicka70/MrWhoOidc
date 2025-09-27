using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin.ProviderKeys;
using MrWhoOidc.WebAuth.Security;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class ProviderKeysPageModelTests
{
    private sealed class DummyJwksCache : IPublicJwksCache
    {
        public Task<(string etag, string json)> GetClientAsync(string clientId, CancellationToken ct) => Task.FromResult(("etag", "{\"keys\":[]}"));
        public Task<(string etag, string json)> GetProviderAsync(string providerName, CancellationToken ct) => Task.FromResult(("etag", "{\"keys\":[]}"));
        public Task<(string etag, string json)> GetAllProvidersAsync(CancellationToken ct) => Task.FromResult(("etag", "{\"keys\":[]}"));
        public void InvalidateClient(string clientId) { }
        public void InvalidateProvider(string providerName) { }
        public void InvalidateAllProviders() { }
    }

    private (AuthDbContext db, IndexModel model) Create()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase("pk-page-" + Guid.NewGuid().ToString("N"))
            .Options;
        var db = new AuthDbContext(options);
        var model = new IndexModel(db, new DummyJwksCache());
        return (db, model);
    }

    [TestMethod]
    public void InputModel_Default_Publishable_Is_True()
    {
        var (_, model) = Create();
        Assert.IsNotNull(model.Input, "Input model should be initialized");
        Assert.IsTrue(model.Input.Publishable, "Expected default Publishable=true on InputModel");
    }
}
