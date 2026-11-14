using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using StackExchange.Redis;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RedisResilienceTests
{
    [TestMethod]
    public async Task DPoPReplayCache_FailsClosed_WhenTheStoreIsUnavailable()
    {
        var cache = new StoreDPoPReplayCache(
            ThrowingStore(),
            NullLogger<StoreDPoPReplayCache>.Instance);

        Assert.IsFalse(await cache.TryAddAsync("jti-1", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public async Task JarReplayCache_FailsClosed_WhenTheStoreIsUnavailable()
    {
        var cache = new StoreJarReplayCache(
            ThrowingStore(),
            NullLogger<StoreJarReplayCache>.Instance);

        Assert.IsFalse(await cache.TryAddAsync("jti-1", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public async Task JarReplayCache_OversizeJti_StillRoundTripsThroughTheStore()
    {
        var cache = new StoreJarReplayCache(
            new MemoryDistributedStateStore(),
            NullLogger<StoreJarReplayCache>.Instance);
        var key = "jar:client:" + new string('j', 2_000);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);

        Assert.IsTrue(await cache.TryAddAsync(key, expiresAt), "A long jti must not disable replay protection.");
        Assert.IsFalse(await cache.TryAddAsync(key, expiresAt), "The same long jti must still be detected as a replay.");
    }

    [TestMethod]
    public void DPoPNonceStoreKey_DoesNotContainRawClientIp()
    {
        var key = StoreDPoPNonceStore.Key("/token", "203.0.113.42", "jkt-1");

        Assert.IsFalse(key.Contains("203.0.113.42", StringComparison.Ordinal), $"Raw IP leaked into key '{key}'.");
        StringAssert.StartsWith(key, "dpop:nonce:/token:");
        StringAssert.EndsWith(key, ":jkt-1");
        Assert.AreEqual(key, StoreDPoPNonceStore.Key("/token", "203.0.113.42", "jkt-1"), "Key must be deterministic.");
        Assert.AreNotEqual(key, StoreDPoPNonceStore.Key("/token", "203.0.113.43", "jkt-1"));
    }

    [TestMethod]
    public void AddMrWhoOidcRedis_DoesNotThrow_WhenRedisIsUnreachable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = "127.0.0.1:1,connectTimeout=200"
            })
            .Build();
        var services = new ServiceCollection();

        using var mux = services.AddMrWhoOidcRedis(configuration);

        Assert.IsNotNull(mux);
        Assert.IsFalse(mux!.IsConnected);
    }

    [TestMethod]
    public void ProviderDefaultsToDatabase_WhenUnset()
    {
        var options = new DistributedStateOptions();
        Assert.AreEqual(DistributedStateProvider.Database, options.ResolveProvider());
    }

    [TestMethod]
    [DataRow("database", "Database")]
    [DataRow("Db", "Database")]
    [DataRow("Redis", "Redis")]
    [DataRow("Memory", "Memory")]
    [DataRow("InMemory", "Memory")]
    public void ProviderParsing_IsCaseInsensitive(string value, string expected)
    {
        var options = new DistributedStateOptions { Provider = value };

        Assert.AreEqual(Enum.Parse<DistributedStateProvider>(expected), options.ResolveProvider());
    }

    [TestMethod]
    public void ProviderParsing_RejectsUnknownValue()
    {
        var options = new DistributedStateOptions { Provider = "sqlite" };

        Assert.ThrowsExactly<InvalidOperationException>(() => options.ResolveProvider());
    }

    private static IDistributedStateStore ThrowingStore()
    {
        var store = new Mock<IDistributedStateStore>();
        store
            .Setup(s => s.TryAddAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"));
        return store.Object;
    }
}
