using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class DistributedLoginRateLimiterTests
{
    [TestMethod]
    public async Task RegistersFailuresInSharedDistributedCache()
    {
        var store = new MemoryDistributedStateStore();
        var tenantId = Guid.NewGuid();
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(tenantId, "tenant-a");
        var limiterA = new DistributedLoginRateLimiter(store, tenantAccessor);
        var limiterB = new DistributedLoginRateLimiter(store, tenantAccessor);
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await limiterA.RegisterFailedAttemptAsync(httpContext, "alice@example.test");
        }

        Assert.IsTrue(await limiterB.IsLockedOutAsync(httpContext, "alice@example.test"));

        await limiterB.ClearAsync(httpContext, "alice@example.test");

        Assert.IsFalse(await limiterA.IsLockedOutAsync(httpContext, "alice@example.test"));
    }
}
