using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MrWhoOidc.WebAuth.Infrastructure;
using StackExchange.Redis;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class RateLimitHeadersIntegrationTests
{
    private static async Task<IHost?> CreateHostWithRedisAsync(string? redisConnection)
    {
        if (string.IsNullOrWhiteSpace(redisConnection)) return null;

        // Try to connect to Redis first; if it fails, skip tests by returning null
        IConnectionMultiplexer mux;
        try
        {
            mux = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        }
        catch
        {
            return null;
        }

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    // Needed for UseRouting/UseEndpoints below
                    services.AddRouting();
                    // Provide logging for middleware diagnostics (optional but helpful)
                    services.AddLogging();
                    services.AddSingleton<IConnectionMultiplexer>(mux);
                });
                webBuilder.Configure(app =>
                {
                    // Only the distributed limiter middleware and two endpoints we care about
                    app.UseMiddleware<DistributedRateLimiterMiddleware>();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/token", async ctx =>
                        {
                            await ctx.Response.WriteAsync("ok");
                        });
                        endpoints.MapPost("/introspect", async ctx =>
                        {
                            await ctx.Response.WriteAsync("ok");
                        });
                    });
                });
            });

        return await builder.StartAsync();
    }

    private static AuthenticationHeaderValue Basic(string id, string secret)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    [TestMethod]
    public async Task TokenExchange_RedisRateLimit_EmitsHeaders_On429()
    {
        var redis = Environment.GetEnvironmentVariable("ConnectionStrings__redis")
                    ?? Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__REDIS")
                    ?? "localhost:6379";

        var host = await CreateHostWithRedisAsync(redis);
        if (host is null)
        {
            Assert.Inconclusive("Redis not available; skipping Redis rate-limit header test.");
            return;
        }

        using var _ = host;
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Basic("client1", "secret");

        // Adaptive: issue requests until 429 or maxAttempts (limit + buffer) reached to reduce timing flakiness
        const int expectedLimit = 40;
        int maxAttempts = expectedLimit + 5; // small buffer
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = "dummy",
            ["audience"] = "api-x"
        };

        HttpResponseMessage? last = null;
        int attempts = 0;
        for (; attempts < maxAttempts; attempts++)
        {
            last = await client.PostAsync("/token", new FormUrlEncodedContent(form));
            if (last.StatusCode == HttpStatusCode.TooManyRequests) break;
            // tiny delay to allow Redis TTL window to apply consistently (helps on slower CI machines)
            await Task.Delay(10);
        }

        Assert.IsNotNull(last, "No response captured");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, last!.StatusCode, $"Did not observe 429 within {maxAttempts} attempts (observed {attempts}).");
        Assert.IsTrue(last.Headers.Contains("Retry-After"), "Missing Retry-After header");
        Assert.IsTrue(last.Headers.Contains("X-RateLimit-Limit"), "Missing X-RateLimit-Limit header");
        Assert.IsTrue(last.Headers.Contains("X-RateLimit-Remaining"), "Missing X-RateLimit-Remaining header");
        Assert.IsTrue(last.Headers.Contains("X-RateLimit-Reset"), "Missing X-RateLimit-Reset header");

        var limit = last.Headers.GetValues("X-RateLimit-Limit").FirstOrDefault();
        var remaining = last.Headers.GetValues("X-RateLimit-Remaining").FirstOrDefault();
        Assert.AreEqual(expectedLimit.ToString(), limit, $"Expected token-exchange limit {expectedLimit}");
        Assert.AreEqual("0", remaining, "Expected 0 remaining after exceeding limit");
    }

    [TestMethod]
    public async Task Introspection_RedisRateLimit_EmitsHeaders_On429()
    {
        var redis = Environment.GetEnvironmentVariable("ConnectionStrings__redis")
                    ?? Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__REDIS")
                    ?? "localhost:6379";

        var host = await CreateHostWithRedisAsync(redis);
        if (host is null)
        {
            Assert.Inconclusive("Redis not available; skipping Redis rate-limit header test.");
            return;
        }

        using var _ = host;
        var client = host.GetTestClient();

        // Adaptive loop similar to token-exchange
    const int expectedLimit = 80;
    // Allow a larger buffer so slow Redis or latency doesn't prevent hitting the limit within attempts
    int maxAttempts = expectedLimit + 40; // generous buffer to reliably reach 429
        HttpResponseMessage? last = null;
        int attempts = 0;
        for (; attempts < maxAttempts; attempts++)
        {
            last = await client.PostAsync("/introspect", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }));
            if (last.StatusCode == HttpStatusCode.TooManyRequests) break;
            await Task.Delay(10);
        }

        Assert.IsNotNull(last, "No response captured");
        if (last!.StatusCode != HttpStatusCode.TooManyRequests)
        {
            var limitHeader = last.Headers.Contains("X-RateLimit-Limit") ? string.Join(',', last.Headers.GetValues("X-RateLimit-Limit")) : "<missing>";
            var remainingHeader = last.Headers.Contains("X-RateLimit-Remaining") ? string.Join(',', last.Headers.GetValues("X-RateLimit-Remaining")) : "<missing>";
            Assert.Fail($"Did not observe 429 within {maxAttempts} attempts (observed {attempts}). Final status={last.StatusCode}, X-RateLimit-Limit={limitHeader}, Remaining={remainingHeader}");
        }
        Assert.IsTrue(last.Headers.Contains("Retry-After"), "Missing Retry-After header");
        Assert.IsTrue(last.Headers.Contains("X-RateLimit-Limit"), "Missing X-RateLimit-Limit header");
        Assert.IsTrue(last.Headers.Contains("X-RateLimit-Remaining"), "Missing X-RateLimit-Remaining header");
        Assert.IsTrue(last.Headers.Contains("X-RateLimit-Reset"), "Missing X-RateLimit-Reset header");

        var limit = last.Headers.GetValues("X-RateLimit-Limit").FirstOrDefault();
        var remaining = last.Headers.GetValues("X-RateLimit-Remaining").FirstOrDefault();
        Assert.AreEqual(expectedLimit.ToString(), limit, $"Expected introspect limit {expectedLimit}");
        Assert.AreEqual("0", remaining, "Expected 0 remaining after exceeding limit");
    }
}
