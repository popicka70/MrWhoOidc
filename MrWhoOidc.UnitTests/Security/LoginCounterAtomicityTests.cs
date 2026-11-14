using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// 2026-10-04 assessment: the login rate limiter and the account lockout counter were read-modify-write updates,
/// so concurrent guesses overwrote each other's increments and a parallel burst never reached the threshold.
/// </summary>
[TestClass]
public sealed class LoginCounterAtomicityTests
{
    [TestMethod]
    public async Task AccountLockout_ConcurrentFailures_AreAllCounted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lockout-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 60 }.ToString();
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connectionString).Options;
        try
        {
            Guid accountId;
            await using (var setup = new AuthDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                var account = new UserAccount { Username = "alice", PasswordHash = "h" };
                setup.UserAccounts.Add(account);
                await setup.SaveChangesAsync();
                accountId = account.Id;
            }

            const int attempts = 24;
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, attempts).Select(_ => Task.Run(async () =>
            {
                await using var db = new AuthDbContext(options);
                var service = new GlobalAuthenticationService(
                    new UserAccountService(db), new DummyHasher(), new GlobalAuthMetrics(),
                    NullLogger<GlobalAuthenticationService>.Instance, db);
                start.Wait();
                await service.RecordFailedAttemptAsync(accountId);
            })).ToArray();
            start.Set();
            await Task.WhenAll(tasks);

            await using var verify = new AuthDbContext(options);
            var stored = await verify.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
            Assert.AreEqual(attempts, stored.FailedLoginAttempts, "every failed attempt must be counted");
            Assert.IsNotNull(stored.LockedOutUntil);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    [TestMethod]
    public async Task AccountLockout_LocksExactlyAtTheThreshold()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var account = new UserAccount { Username = "bob", PasswordHash = "h" };
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();
        var service = new GlobalAuthenticationService(new UserAccountService(db), new DummyHasher(), new GlobalAuthMetrics(),
            NullLogger<GlobalAuthenticationService>.Instance, db);

        for (var i = 0; i < 4; i++) await service.RecordFailedAttemptAsync(account.Id);
        Assert.IsFalse(await service.IsLockedOutAsync(account.Id));

        await service.RecordFailedAttemptAsync(account.Id);
        Assert.IsTrue(await service.IsLockedOutAsync(account.Id));
    }

    [TestMethod]
    public async Task DistributedLimiter_UsesASingleAtomicIncrement()
    {
        var store = new Mock<IDistributedStateStore>(MockBehavior.Strict);
        store.Setup(s => s.IncrementAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);
        store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("5");
        var limiter = new DistributedLoginRateLimiter(store.Object, MockTenantAccessor.CreateWithTenant(Guid.NewGuid(), "t"));
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");

        await limiter.RegisterFailedAttemptAsync(http, "alice");

        Assert.IsTrue(await limiter.IsLockedOutAsync(http, "alice"));
        store.Verify(s => s.IncrementAsync(
            It.Is<string>(k => k.StartsWith("login-rl:", StringComparison.Ordinal)),
            TimeSpan.FromMinutes(5),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void Registration_UsesTheDistributedStoreLimiter()
    {
        var limiter = BuildLimiter();

        Assert.IsInstanceOfType<DistributedLoginRateLimiter>(limiter);
    }

    private static ILoginRateLimiter BuildLimiter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantAccessor>(MockTenantAccessor.CreateWithTenant(Guid.NewGuid(), "t"));
        services.AddSingleton<IDistributedStateStore>(new MemoryDistributedStateStore());
        services.AddScoped(LoginRateLimit.Create);
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ILoginRateLimiter>();
    }

    [TestMethod]
    public async Task MemoryBackend_ConcurrentFailures_ReachTheThreshold()
    {
        var store = new MemoryDistributedStateStore();
        var limiter = new DistributedLoginRateLimiter(store, MockTenantAccessor.CreateWithTenant(Guid.NewGuid(), "t"));
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.8");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => limiter.RegisterFailedAttemptAsync(http, "alice"))));

        Assert.IsTrue(await limiter.IsLockedOutAsync(http, "alice"));
    }

    private sealed class DummyHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }
}
