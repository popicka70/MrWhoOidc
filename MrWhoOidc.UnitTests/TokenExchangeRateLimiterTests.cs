using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.WebAuth.TokenEndpoint.RateLimiting;
using StackExchange.Redis;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class TokenExchangeRateLimiterTests
{
    private static readonly System.Guid TenantA = System.Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly System.Guid TenantB = System.Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static IOptions<TokenExchangeRateLimitOptions> Opts(int perMinute, bool enabled = true)
        => Options.Create(new TokenExchangeRateLimitOptions { Enabled = enabled, PerClientPerMinute = perMinute });

    [TestMethod]
    public async Task InMemory_SameClientIdInAnotherTenant_HasIndependentBudget()
    {
        var limiter = new InMemoryTokenExchangeRateLimiter(Opts(1));
        Assert.IsTrue((await limiter.ShouldAllowAsync(TenantA, "shared-client")).Allowed);
        Assert.IsFalse((await limiter.ShouldAllowAsync(TenantA, "shared-client")).Allowed);

        var otherTenant = await limiter.ShouldAllowAsync(TenantB, "shared-client");

        Assert.IsTrue(otherTenant.Allowed, "Exhausting tenant A's budget must not throttle the same client_id in tenant B");
    }

    [TestMethod]
    public void Redis_Key_IsTenantQualified()
    {
        var now = new System.DateTimeOffset(2026, 10, 5, 12, 34, 0, System.TimeSpan.Zero);

        var a = RedisTokenExchangeRateLimiter.BuildKey(TenantA, "shared-client", now);
        var b = RedisTokenExchangeRateLimiter.BuildKey(TenantB, "shared-client", now);

        Assert.AreEqual("te:rl:11111111111111111111111111111111:shared-client:202610051234", a);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public async Task InMemory_Allows_UnderLimit()
    {
        var limiter = new InMemoryTokenExchangeRateLimiter(Opts(3));
        var r1 = await limiter.ShouldAllowAsync(TenantA, "clientA");
        var r2 = await limiter.ShouldAllowAsync(TenantA, "clientA");
        var r3 = await limiter.ShouldAllowAsync(TenantA, "clientA");
        Assert.IsTrue(r1.Allowed);
        Assert.IsTrue(r2.Allowed);
        Assert.IsTrue(r3.Allowed);
    }

    [TestMethod]
    public async Task InMemory_Blocks_OverLimit()
    {
        var limiter = new InMemoryTokenExchangeRateLimiter(Opts(2));
        _ = await limiter.ShouldAllowAsync(TenantA, "clientA");
        _ = await limiter.ShouldAllowAsync(TenantA, "clientA");
        var r3 = await limiter.ShouldAllowAsync(TenantA, "clientA");
        Assert.IsFalse(r3.Allowed, "Expected block on third request over limit 2");
        Assert.IsTrue(r3.RetryAfterSeconds.HasValue && r3.RetryAfterSeconds.Value > 0);
    }

    [TestMethod]
    public async Task InMemory_Disabled_Bypasses()
    {
        var limiter = new InMemoryTokenExchangeRateLimiter(Opts(1, enabled: false));
        for (int i = 0; i < 10; i++)
        {
            var r = await limiter.ShouldAllowAsync(TenantA, "clientA");
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
        _ = await limiter.ShouldAllowAsync(TenantA, "clientB");
        _ = await limiter.ShouldAllowAsync(TenantA, "clientB");
        var r3 = await limiter.ShouldAllowAsync(TenantA, "clientB");
        Assert.IsFalse(r3.Allowed, "Expected Redis limiter to block over limit");
        Assert.IsTrue(r3.RetryAfterSeconds.HasValue && r3.RetryAfterSeconds.Value > 0);
    }
}
