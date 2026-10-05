using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.UnitTests.Testing;
using MrWhoOidc.WebAuth.Infrastructure.Health;
using MrWhoOidc.WebAuth.Infrastructure.Startup;

namespace MrWhoOidc.UnitTests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class HealthAndTopologyTests
{
    private static Lazy<WebApplicationFactory<Program>> s_factory = null!;
    private static WebApplicationFactory<Program> Factory => s_factory.Value;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        s_factory = new Lazy<WebApplicationFactory<Program>>(() =>
            (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory());
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        if (s_factory?.IsValueCreated == true)
        {
            s_factory.Value.Dispose();
        }
    }

    [TestMethod]
    public async Task Ready_Endpoint_Is_Anonymous_And_Returns_Only_Status()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "status" }, properties);
        Assert.AreEqual("healthy", document.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Ready_Endpoint_Returns_503_Without_Details_When_A_Readiness_Check_Fails()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHealthChecks().AddCheck(
                "failing-dependency",
                () => HealthCheckResult.Unhealthy("secret-internal-detail"),
                tags: [ReadinessTags.Ready])));
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex("secret-internal-detail|failing-dependency"));
        using var document = JsonDocument.Parse(body);
        Assert.AreEqual("unhealthy", document.RootElement.GetProperty("status").GetString());

        // Liveness must not depend on readiness dependencies.
        using var live = await client.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
    }

    [TestMethod]
    public async Task Liveness_Endpoint_Does_Not_Report_Database_State()
    {
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("healthy", document.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(document.RootElement.TryGetProperty("database", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("bootstrapRequired", out _));
    }

    [TestMethod]
    public void Startup_Fails_When_MultiInstance_Is_Set_Without_Redis()
    {
        using var factory = TestWebAppFactory.CreateInMemory()
            .WithWebHostBuilder(b => b
                .UseSetting(DeploymentTopologyGuard.MultiInstanceKey, "true")
                // CI exports ConnectionStrings__redis for the Redis-backed tests; blank it so Redis is really absent here.
                .UseSetting("ConnectionStrings:redis", ""));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => _ = factory.Server);
        StringAssert.Contains(ex.Message, "Deployment:MultiInstance");
    }

    [TestMethod]
    public void Guard_Throws_For_MultiInstance_Without_Redis()
    {
        var config = Config(("Deployment:MultiInstance", "true"));
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => DeploymentTopologyGuard.Validate(config, redisConfigured: false, new CapturingLogger()));
        StringAssert.Contains(ex.Message, "ConnectionStrings:redis");
    }

    [TestMethod]
    public void Guard_Allows_MultiInstance_With_Redis_And_Logs_Nothing()
    {
        var logger = new CapturingLogger();
        DeploymentTopologyGuard.Validate(Config(("Deployment:MultiInstance", "true")), redisConfigured: true, logger);
        Assert.AreEqual(0, logger.Warnings.Count);
    }

    [TestMethod]
    public void Guard_Warns_When_InMemory_Fallbacks_Are_Active()
    {
        var logger = new CapturingLogger();
        DeploymentTopologyGuard.Validate(Config(), redisConfigured: false, logger);
        Assert.AreEqual(1, logger.Warnings.Count);
        StringAssert.Contains(logger.Warnings[0], "JAR (request object) replay cache");
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
