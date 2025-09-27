using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Background;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.UnitTests;

[TestClass]
[DoNotParallelize]
public class BackchannelAlertSamplerTests
{
    private class TestClock : ISystemClock { public DateTimeOffset UtcNow => _now; public void Advance(TimeSpan span) => _now += span; private DateTimeOffset _now = DateTimeOffset.UtcNow; }

    private AuthDbContext CreateContext(string dbName)
    {
        var opts = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var ctx = new AuthDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static BackchannelLogoutNotification Make(string status, DateTimeOffset created, DateTimeOffset? lastAttempt = null) => new()
    {
        ClientDbId = Guid.NewGuid(),
        ClientId = "c1",
        TargetUri = "https://rp.example/back",
        LogoutToken = "t",
        Status = status,
        CreatedAt = created,
        LastAttemptAt = lastAttempt,
        MaxAttempts = 5
    };

    [TestMethod]
    public async Task SustainedFailureRate_EmitsOnlyAfterRequiredSamples()
    {
        var alerts = new CollectingAlertPublisher();
        var clock = new TestClock();

        var dbName = Guid.NewGuid().ToString();
        await using var ctx = CreateContext(dbName);

        // Insert failures over time
        for (int i = 0; i < 5; i++)
        {
            ctx.BackchannelLogoutNotifications.Add(Make("failed", clock.UtcNow.AddMinutes(-i), clock.UtcNow.AddMinutes(-i)));
        }
        await ctx.SaveChangesAsync();

        var dbFactory = new TestDbFactory<AuthDbContext>(() => CreateContext(dbName));
        var metrics = new OidcMetrics();
        var opts = Options.Create(new BackchannelAlertOptions
        {
            Enabled = true,
            FailureRatePercent = 50,
            SampleIntervalSeconds = 30,
            ConsecutiveMinutes = 2,
            LookbackMinutes = 10,
            CooldownSeconds = 0
        });
        var optMonitor = Mock.Of<IOptionsMonitor<BackchannelAlertOptions>>(m => m.CurrentValue == opts.Value);
        var runtime = new BackchannelRuntimeState { PendingBacklog = 0 };
        var sampler = new BackchannelAlertSampler(dbFactory, metrics, Mock.Of<Microsoft.Extensions.Logging.ILogger<BackchannelAlertSampler>>(), alerts, optMonitor, runtime, clock);

        // Need requiredSamples = ceil( (2*60)/30 ) = 4 samples
        for (int s = 0; s < 3; s++)
        {
            await sampler.TickAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        Assert.AreEqual(0, alerts.Published.Count, "No alert before sustained window reached");

        await sampler.TickAsync(CancellationToken.None); // 4th sample triggers
        Assert.IsTrue(alerts.Published.Any(a => a.Type == "bcl.alert.failure_rate"), "Failure rate alert emitted after sustained threshold");
    }

    [TestMethod]
    public async Task BacklogThreshold_WithSustain()
    {
        var alerts = new CollectingAlertPublisher();
        var clock = new TestClock();

        var dbName = Guid.NewGuid().ToString();
        await using var ctx = CreateContext(dbName);

        // Insert some succeeded items so emitted count non-zero
        for (int i = 0; i < 10; i++)
        {
            ctx.BackchannelLogoutNotifications.Add(Make("succeeded", clock.UtcNow.AddMinutes(-1), clock.UtcNow.AddMinutes(-1)));
        }
        await ctx.SaveChangesAsync();

        var dbFactory = new TestDbFactory<AuthDbContext>(() => CreateContext(dbName));
        var metrics = new OidcMetrics();
        var opts = Options.Create(new BackchannelAlertOptions
        {
            Enabled = true,
            OutboxBacklogThreshold = 10,
            SampleIntervalSeconds = 60,
            ConsecutiveMinutes = 1,
            LookbackMinutes = 5,
            CooldownSeconds = 0
        });
        var optMonitor = Mock.Of<IOptionsMonitor<BackchannelAlertOptions>>(m => m.CurrentValue == opts.Value);
        var runtime = new BackchannelRuntimeState { PendingBacklog = 12 };
        var sampler = new BackchannelAlertSampler(dbFactory, metrics, Mock.Of<Microsoft.Extensions.Logging.ILogger<BackchannelAlertSampler>>(), alerts, optMonitor, runtime, clock);

        await sampler.TickAsync(CancellationToken.None); // requiredSamples = 1 ( (1*60)/60 )
        Assert.IsTrue(alerts.Published.Any(a => a.Type == "bcl.alert.backlog"), "Backlog alert emitted immediately when sustained=1");
    }

    [TestMethod]
    public async Task Cooldown_PreventsRapidRepeatAlerts()
    {
        var alerts = new CollectingAlertPublisher();
        var clock = new TestClock();
        // Use backlog metric (simpler: independent of DB counts) for deterministic cooldown verification
        var dbName = Guid.NewGuid().ToString();
        await using var ctx = CreateContext(dbName); // empty DB is fine
        var dbFactory = new TestDbFactory<AuthDbContext>(() => CreateContext(dbName));
        var metrics = new OidcMetrics();
        var opts = Options.Create(new BackchannelAlertOptions
        {
            Enabled = true,
            FailureRatePercent = 0, // disable failure metric
            OutboxBacklogThreshold = 10,
            SampleIntervalSeconds = 30,
            ConsecutiveMinutes = 0, // zero means sustain requirement collapses to 1 sample
            LookbackMinutes = 5,
            CooldownSeconds = 300 // 5 min cooldown
        });
        var optMonitor = Mock.Of<IOptionsMonitor<BackchannelAlertOptions>>(m => m.CurrentValue == opts.Value);
        var runtime = new BackchannelRuntimeState { PendingBacklog = 15 }; // exceed threshold
        var sampler = new BackchannelAlertSampler(dbFactory, metrics, Mock.Of<Microsoft.Extensions.Logging.ILogger<BackchannelAlertSampler>>(), alerts, optMonitor, runtime, clock);

        await sampler.TickAsync(CancellationToken.None); // first sample triggers (sustain satisfied)
        Assert.AreEqual(1, alerts.Published.Count(a => a.Type == "bcl.alert.backlog"));

        // Advance less than cooldown and sample again - should not emit
        clock.Advance(TimeSpan.FromSeconds(60));
        await sampler.TickAsync(CancellationToken.None);
        Assert.AreEqual(1, alerts.Published.Count(a => a.Type == "bcl.alert.backlog"), "No second alert within cooldown");

        // Advance beyond cooldown (add +1s guard to avoid edge precision) and sample again - should emit second alert
        clock.Advance(TimeSpan.FromSeconds(301));
        await sampler.TickAsync(CancellationToken.None);
        Assert.AreEqual(2, alerts.Published.Count(a => a.Type == "bcl.alert.backlog"), "Second alert after cooldown elapsed");
    }

    [TestMethod]
    public async Task SustainZero_EmitsImmediately_ForAllEnabledMetrics()
    {
        var alerts = new CollectingAlertPublisher();
        var clock = new TestClock();
        var dbName = Guid.NewGuid().ToString();
        await using var ctx = CreateContext(dbName);

        // Seed failures for failure metric evaluation
        for (int i = 0; i < 2; i++)
        {
            ctx.BackchannelLogoutNotifications.Add(Make("failed", clock.UtcNow.AddMinutes(-i), clock.UtcNow.AddMinutes(-i)));
        }
        await ctx.SaveChangesAsync();

        var dbFactory = new TestDbFactory<AuthDbContext>(() => CreateContext(dbName));
        var metrics = new OidcMetrics();
        var opts = Options.Create(new BackchannelAlertOptions
        {
            Enabled = true,
            FailureRatePercent = 50,
            OutboxBacklogThreshold = 5,
            SampleIntervalSeconds = 30,
            ConsecutiveMinutes = 0,
            LookbackMinutes = 5,
            CooldownSeconds = 0
        });
        var optMonitor = Mock.Of<IOptionsMonitor<BackchannelAlertOptions>>(m => m.CurrentValue == opts.Value);
        var runtime = new BackchannelRuntimeState { PendingBacklog = 10 }; // exceed threshold
        var sampler = new BackchannelAlertSampler(dbFactory, metrics, Mock.Of<Microsoft.Extensions.Logging.ILogger<BackchannelAlertSampler>>(), alerts, optMonitor, runtime, clock);

        await sampler.TickAsync(CancellationToken.None);
        Assert.IsTrue(alerts.Published.Any(a => a.Type == "bcl.alert.backlog"), "Backlog alert should emit immediately with sustain=0");
        Assert.IsTrue(alerts.Published.Any(a => a.Type == "bcl.alert.failure_rate"), "Failure rate alert should emit immediately with sustain=0");
    }

    private sealed class CollectingAlertPublisher : IAlertPublisher
    {
        public record AlertRec(string Type, object Payload);
        public List<AlertRec> Published { get; } = new();
        public Task PublishAsync(string type, object payload, CancellationToken ct) { Published.Add(new AlertRec(type, payload)); return Task.CompletedTask; }
    }

    private sealed class TestDbFactory<T> : IDbContextFactory<T> where T : DbContext
    {
        private readonly Func<T> _factory;
        public TestDbFactory(Func<T> factory) => _factory = factory;
        public T CreateDbContext() => _factory();
        public Task<T> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(_factory());
    }
}
