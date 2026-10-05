using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.TokenEndpoint.Grants;
using StackExchange.Redis;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class RateLimitPartitionKeyTests
{
    [TestMethod]
    public void ForClient_SameClientId_DiffersByIpAndTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var baseline = RateLimitPartitionKeys.ForClient(CreateContext("/token", "10.0.0.1", tenantA), "victim");
        var otherIp = RateLimitPartitionKeys.ForClient(CreateContext("/token", "10.0.0.2", tenantA), "victim");
        var otherTenant = RateLimitPartitionKeys.ForClient(CreateContext("/token", "10.0.0.1", tenantB), "victim");
        var same = RateLimitPartitionKeys.ForClient(CreateContext("/token", "10.0.0.1", tenantA), "victim");

        Assert.AreEqual(baseline, same);
        Assert.AreNotEqual(baseline, otherIp);
        Assert.AreNotEqual(baseline, otherTenant);
        Assert.IsFalse(baseline.Contains("10.0.0.1", StringComparison.Ordinal), "Raw IP must not appear in the key.");
    }

    [TestMethod]
    public void ForClient_FallsBackToTenantPathSlug_WhenTenantNotResolved()
    {
        var acme = RateLimitPartitionKeys.ForClient(CreateContext("/t/acme/token", "10.0.0.1", tenantId: null), "c1");
        var globex = RateLimitPartitionKeys.ForClient(CreateContext("/t/globex/token", "10.0.0.1", tenantId: null), "c1");

        Assert.AreNotEqual(acme, globex);
    }

    [TestMethod]
    public void GetClientId_DoesNotReadRequestBody()
    {
        var context = CreateContext("/par", "10.0.0.1", Guid.NewGuid());
        context.Request.ContentType = "application/x-www-form-urlencoded";
        var body = new ThrowingStream();
        context.Request.Body = body;

        Assert.IsNull(RateLimitPartitionKeys.GetClientId(context));
        Assert.IsFalse(body.WasRead);

        context.Items[RateLimitPartitionKeys.FormClientIdItemKey] = "stashed-client";
        Assert.AreEqual("stashed-client", RateLimitPartitionKeys.GetClientId(context));

        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("basic-client:secret"));
        Assert.AreEqual("basic-client", RateLimitPartitionKeys.GetClientId(context));
        Assert.IsFalse(body.WasRead);
    }

    [TestMethod]
    [DataRow("/token")]
    [DataRow("/introspect")]
    [DataRow("/par")]
    [DataRow("/revoke")]
    public async Task Middleware_AttackerSendingVictimClientId_DoesNotDrainVictimBudget(string endpoint)
    {
        var tenant = Guid.NewGuid();
        var middleware = new DistributedRateLimiterMiddleware(_ => Task.CompletedTask, CreateCountingRedis(), NullLogger<DistributedRateLimiterMiddleware>.Instance);

        // Attacker exhausts "victim"'s budget from its own IP (well over every per-endpoint limit).
        for (var i = 0; i < 150; i++)
        {
            await middleware.InvokeAsync(CreateFormContext(endpoint, "203.0.113.66", tenant, "victim"));
        }
        var attackerCtx = CreateFormContext(endpoint, "203.0.113.66", tenant, "victim");
        await middleware.InvokeAsync(attackerCtx);
        Assert.AreEqual(StatusCodes.Status429TooManyRequests, attackerCtx.Response.StatusCode, "Sanity: the attacker itself is throttled.");

        var victimCtx = CreateFormContext(endpoint, "198.51.100.7", tenant, "victim");
        await middleware.InvokeAsync(victimCtx);

        Assert.AreNotEqual(StatusCodes.Status429TooManyRequests, victimCtx.Response.StatusCode, "The real client on another IP must keep its own budget.");
    }

    [TestMethod]
    [DataRow("abc-123", "abc-123")]
    [DataRow("req.1:part_2", "req.1:part_2")]
    [DataRow("trace:01HXYZ.abc-def", "trace:01HXYZ.abc-def")]
    [DataRow("evil\r\nINFO forged entry", null)]
    [DataRow("with space", null)]
    [DataRow("", null)]
    public void SanitizeCorrelationId_AcceptsOnlySafeShortValues(string input, string? expected)
    {
        Assert.AreEqual(expected, TokenExchangeGrantHandler.SanitizeCorrelationId(input));
    }

    [TestMethod]
    public void SanitizeCorrelationId_RejectsOverlongValue()
    {
        Assert.IsNotNull(TokenExchangeGrantHandler.SanitizeCorrelationId(new string('a', 64)));
        Assert.IsNull(TokenExchangeGrantHandler.SanitizeCorrelationId(new string('a', 65)));
    }

    private static DefaultHttpContext CreateContext(string path, string ip, Guid? tenantId)
    {
        var services = new ServiceCollection();
        var accessor = new TenantAccessor();
        if (tenantId is { } id)
        {
            accessor.SetTenant(new TenantContext { TenantId = id, Slug = "t" + id.ToString("N")[..6] });
        }
        services.AddSingleton<ITenantAccessor>(accessor);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    private static DefaultHttpContext CreateFormContext(string path, string ip, Guid tenantId, string clientId)
    {
        var context = CreateContext(path, ip, tenantId);
        var body = Encoding.UTF8.GetBytes($"client_id={clientId}&grant_type=client_credentials&token=x");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        // Unauthenticated at limiter time: anyone can claim this client_id with a wrong secret.
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:wrong-secret"));
        return context;
    }

    private static IConnectionMultiplexer CreateCountingRedis()
    {
        var counts = new ConcurrentDictionary<string, long>();
        var db = new Mock<IDatabase>();
        db.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string _, RedisKey[] keys, RedisValue[] _, CommandFlags _) =>
                Task.FromResult(RedisResult.Create((RedisValue)counts.AddOrUpdate(keys[0].ToString(), 1, (_, c) => c + 1))));
        db.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(TimeSpan.FromSeconds(30));
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        return mux.Object;
    }

    private sealed class ThrowingStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { WasRead = true; throw new InvalidOperationException("body read"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
