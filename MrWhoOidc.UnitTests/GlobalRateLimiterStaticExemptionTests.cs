using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Regression tests for the global per-IP rate limiter's static-asset exemption.
/// <para>
/// The 2026-11-09 E2E run regressed with a 429 storm on static web assets: whole page
/// loads got every stylesheet/font 429'd while the HTML document still returned 200.
/// Root cause: <see cref="PartitionedRateLimiter"/> caches exactly ONE limiter per
/// partition key and never re-invokes the partitioner for a key it has seen. The
/// exemption originally shared the caller-IP key between the no-limiter (statics) and
/// the token bucket (pages), so whichever request type arrived first through an IP
/// decided the limiter for every later request from that IP:
/// <list type="bullet">
///   <item>a PAGE first seeded the token bucket → every static asset then consumed the
///   page budget → parallel page navigation exhausted the 1000/min bucket and statics
///   429'd (the storm);</item>
///   <item>a STATIC first seeded the no-limiter → every page request from that IP was
///   then unlimited.</item>
/// </list>
/// The fix: the static partition carries its own "<c>{ip}:static</c>" cache key.
/// </para>
/// </summary>
[TestClass]
public sealed class GlobalRateLimiterStaticExemptionTests
{
    private static HttpContext RequestFor(string path, string ip = "127.0.0.1")
    {
        var ctx = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse(ip) },
        };
        ctx.Request.Path = path;
        return ctx;
    }

    private static async Task<bool> TryAcquireAsync(PartitionedRateLimiter<HttpContext> limiter, string path)
    {
        using var lease = await limiter.AcquireAsync(RequestFor(path), permitCount: 1);
        return lease.IsAcquired;
    }

    [TestMethod]
    public void IsStaticWebAsset_ClassifiesRootStaticsAndTheFingerprintedBundle()
    {
        // wwwroot prefixes
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/css/site.css"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/css/design-system.mtu937zcj9.css"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/js/site.o353ddbd86.js"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/lib/fontsource/manrope/5.3.0/latin-wght-normal.woff2"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/lib/phosphor-icons/web/2.1.1/src/fill/style.pbvn7di22d.css"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/images/Logo.kft9zq46qw.jpeg"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/uploads/icon.png"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/favicon.ico"));

        // The Razor-compiled bundle is FINGERPRINTED by the web assets pipeline; the
        // pre-fix prefix check missed the fingerprint and the bundle kept consuming the budget.
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/MrWhoOidc.WebAuth.styles.css"));
        Assert.IsTrue(RateLimitingExtensions.IsStaticWebAsset("/MrWhoOidc.WebAuth.5lj3tdxyel.styles.css"));
        Assert.IsFalse(RateLimitingExtensions.IsStaticWebAsset("/MrWhoOidc.WebAuth.somethingElse.js"));

        // Pages/protocol paths are NOT static
        Assert.IsFalse(RateLimitingExtensions.IsStaticWebAsset("/"));
        Assert.IsFalse(RateLimitingExtensions.IsStaticWebAsset("/login"));
        Assert.IsFalse(RateLimitingExtensions.IsStaticWebAsset("/t/default/account/emails"));
        Assert.IsFalse(RateLimitingExtensions.IsStaticWebAsset("/t/default/token"));
        Assert.IsFalse(RateLimitingExtensions.IsStaticWebAsset("/.well-known/openid-configuration"));
        Assert.IsFalse(RateLimitingExtensions.IsStaticWebAsset("/csspage/looks-static-but-isnt"));
    }

    [TestMethod]
    public async Task StaticFirst_DoesNotPoisonThePageBucket()
    {
        // Scenario A of the bug: a static request arrives first. With the shared key it
        // seeded the no-limiter and every later page request from that IP was unlimited.
        var limiter = RateLimitingExtensions.CreateGlobalPerIpLimiter();

        Assert.IsTrue(await TryAcquireAsync(limiter, "/lib/fontsource/manrope/5.3.0/latin-wght-normal.woff2"));

        var acquired = 0;
        for (var i = 0; i < RateLimitingTests.BurstLimit + 50; i++)
        {
            if (await TryAcquireAsync(limiter, "/login")) acquired++;
        }

        // The token bucket (1000/min) must still bound page requests.
        Assert.AreEqual(RateLimitingTests.BurstLimit, acquired,
            "A static request seeding the partition cache must not make page requests unlimited.");

        await limiter.DisposeAsync();
    }

    [TestMethod]
    public async Task PageFirst_DoesNotMakeStaticsConsumeThePageBudget()
    {
        // Scenario B of the bug (the E2E storm): page requests drained the shared bucket
        // first, then every static asset 429'd along with the pages.
        var limiter = RateLimitingExtensions.CreateGlobalPerIpLimiter();

        for (var i = 0; i < RateLimitingTests.BurstLimit + 50; i++)
        {
            await TryAcquireAsync(limiter, "/login");
        }

        // The page budget is exhausted...
        Assert.IsFalse(await TryAcquireAsync(limiter, "/login"));

        // ...but static assets must still pass: they carry their own partition.
        Assert.IsTrue(await TryAcquireAsync(limiter, "/css/design-system.mtu937zcj9.css"));
        Assert.IsTrue(await TryAcquireAsync(limiter, "/MrWhoOidc.WebAuth.5lj3tdxyel.styles.css"));
        Assert.IsTrue(await TryAcquireAsync(limiter, "/lib/phosphor-icons/web/2.1.1/src/fill/Phosphor-Fill.woff2"));

        await limiter.DisposeAsync();
    }

    private static class RateLimitingTests
    {
        public const int BurstLimit = 1000;
    }
}
