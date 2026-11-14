using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;

namespace MrWhoOidc.UnitTests.Infrastructure;

[TestClass]
public sealed class DistributedStateCacheTests
{
    private static DistributedStateCache CreateCache()
        => new(new MemoryDistributedStateStore(), Options.Create(new DistributedStateOptions()));

    [TestMethod]
    public async Task Set_Get_Remove_RoundTripsBinaryPayload()
    {
        var cache = CreateCache();
        var payload = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

        await cache.SetAsync(
            "session:abc",
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });

        CollectionAssert.AreEqual(payload, await cache.GetAsync("session:abc"));

        await cache.RemoveAsync("session:abc");
        Assert.IsNull(await cache.GetAsync("session:abc"));
    }

    [TestMethod]
    public async Task OversizeKeys_AreHashedInsteadOfRejected()
    {
        var cache = CreateCache();
        var key = new string('k', DistributedStateEntryOversizeLength);
        var payload = Encoding.UTF8.GetBytes("value");

        await cache.SetAsync(
            key,
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });

        CollectionAssert.AreEqual(payload, await cache.GetAsync(key));
    }

    [TestMethod]
    public async Task ExpiredEntry_ReturnsNull()
    {
        var cache = CreateCache();
        await cache.SetAsync(
            "short",
            Encoding.UTF8.GetBytes("v"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(150) });

        await Task.Delay(350);

        Assert.IsNull(await cache.GetAsync("short"));
    }

    // Longer than the store's 512-character key bound once the cache prefix is added.
    private const int DistributedStateEntryOversizeLength = 600;
}
