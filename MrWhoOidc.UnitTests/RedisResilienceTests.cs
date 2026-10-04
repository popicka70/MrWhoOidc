using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using StackExchange.Redis;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RedisResilienceTests
{
    [TestMethod]
    public void DPoPReplayCache_FailsClosed_WhenRedisConnectionFails()
    {
        var cache = new RedisDPoPReplayCache(CreateMux(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down", null, CommandStatus.Unknown)));

        Assert.IsFalse(cache.TryAdd("jti-1", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public void DPoPReplayCache_FailsClosed_WhenRedisTimesOut()
    {
        var cache = new RedisDPoPReplayCache(CreateMux(new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent)));

        Assert.IsFalse(cache.TryAdd("jti-1", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public void JarReplayCache_FailsClosed_WhenRedisConnectionFails()
    {
        var cache = new RedisJarReplayCache(CreateMux(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "down", null, CommandStatus.Unknown)));

        Assert.IsFalse(cache.TryAdd("jti-1", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public void JarReplayCache_FailsClosed_WhenRedisTimesOut()
    {
        var cache = new RedisJarReplayCache(CreateMux(new RedisTimeoutException(CommandFlags.None, "timeout", CommandStatus.Sent)));

        Assert.IsFalse(cache.TryAdd("jti-1", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [TestMethod]
    public void DPoPNonceStoreKey_DoesNotContainRawClientIp()
    {
        var key = RedisDPoPNonceStore.Key("/token", "203.0.113.42", "jkt-1");

        Assert.IsFalse(key.Contains("203.0.113.42", StringComparison.Ordinal), $"Raw IP leaked into key '{key}'.");
        StringAssert.StartsWith(key, "dpop:nonce:/token:");
        StringAssert.EndsWith(key, ":jkt-1");
        Assert.AreEqual(key, RedisDPoPNonceStore.Key("/token", "203.0.113.42", "jkt-1"), "Key must be deterministic.");
        Assert.AreNotEqual(key, RedisDPoPNonceStore.Key("/token", "203.0.113.43", "jkt-1"));
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

    private static IConnectionMultiplexer CreateMux(Exception failure)
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<When>()))
            .Throws(failure);
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        return mux.Object;
    }
}
