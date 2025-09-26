using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;
using StackExchange.Redis;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class TokenExchangeRateLimiterTests
{
    private static IOptions<TokenExchangeRateLimitOptions> Opts(int perMinute, bool enabled = true)
        => Options.Create(new TokenExchangeRateLimitOptions { Enabled = enabled, PerClientPerMinute = perMinute });

    [TestMethod]
    public async Task InMemory_Allows_UnderLimit()
    {
        var limiter = new InMemoryTokenExchangeRateLimiter(Opts(3));
        var r1 = await limiter.ShouldAllowAsync("clientA");
        var r2 = await limiter.ShouldAllowAsync("clientA");
        var r3 = await limiter.ShouldAllowAsync("clientA");
        Assert.IsTrue(r1.Allowed);
        Assert.IsTrue(r2.Allowed);
        Assert.IsTrue(r3.Allowed);
    }

    [TestMethod]
    public async Task InMemory_Blocks_OverLimit()
    {
        var limiter = new InMemoryTokenExchangeRateLimiter(Opts(2));
        _ = await limiter.ShouldAllowAsync("clientA");
        _ = await limiter.ShouldAllowAsync("clientA");
        var r3 = await limiter.ShouldAllowAsync("clientA");
        Assert.IsFalse(r3.Allowed, "Expected block on third request over limit 2");
        Assert.IsTrue(r3.RetryAfterSeconds.HasValue && r3.RetryAfterSeconds.Value > 0);
    }

    [TestMethod]
    public async Task InMemory_Disabled_Bypasses()
    {
        var limiter = new InMemoryTokenExchangeRateLimiter(Opts(1, enabled:false));
        for (int i = 0; i < 10; i++)
        {
            var r = await limiter.ShouldAllowAsync("clientA");
            Assert.IsTrue(r.Allowed, "Disabled limiter should always allow");
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Redis_Blocks_OverLimit_IfRedisAvailable()
    {
        var redisConn = System.Environment.GetEnvironmentVariable("ConnectionStrings__redis")
                        ?? System.Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__REDIS")
                        ?? "localhost:6379";
        IConnectionMultiplexer? mux = null;
        try
        {
            mux = await ConnectionMultiplexer.ConnectAsync(redisConn);
        }
        catch
        {
            Assert.Inconclusive("Redis not available; skipping Redis limiter test.");
            return;
        }

        var limiter = new RedisTokenExchangeRateLimiter(mux, Opts(2));
        _ = await limiter.ShouldAllowAsync("clientB");
        _ = await limiter.ShouldAllowAsync("clientB");
        var r3 = await limiter.ShouldAllowAsync("clientB");
        Assert.IsFalse(r3.Allowed, "Expected Redis limiter to block over limit");
        Assert.IsTrue(r3.RetryAfterSeconds.HasValue && r3.RetryAfterSeconds.Value > 0);
    }
}
