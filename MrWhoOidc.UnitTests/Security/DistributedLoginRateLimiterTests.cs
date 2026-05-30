using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

[TestClass]
public sealed class DistributedLoginRateLimiterTests
{
    [TestMethod]
    public async Task RegistersFailuresInSharedDistributedCache()
    {
        var memoryCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var tenantId = Guid.NewGuid();
        var tenantAccessor = MockTenantAccessor.CreateWithTenant(tenantId, "tenant-a");
        var limiterA = new DistributedLoginRateLimiter(memoryCache, tenantAccessor);
        var limiterB = new DistributedLoginRateLimiter(memoryCache, tenantAccessor);
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
