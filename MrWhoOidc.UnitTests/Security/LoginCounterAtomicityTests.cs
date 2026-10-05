using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Services;
using StackExchange.Redis;

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
    public async Task RedisLimiter_CountsWithASingleAtomicIncrement()
    {
        var store = new Dictionary<string, long>();
        var db = new Mock<IDatabase>(MockBehavior.Strict);
        string? script = null;
        RedisValue[]? args = null;
        db.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string s, RedisKey[] keys, RedisValue[] values, CommandFlags _) =>
            {
                script = s;
                args = values;
                lock (store)
                {
                    store[keys[0].ToString()] = store.GetValueOrDefault(keys[0].ToString()) + 1;
                    return Task.FromResult(RedisResult.Create((RedisValue)store[keys[0].ToString()]));
                }
            });
        db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, CommandFlags _) =>
            {
                lock (store)
                {
                    return Task.FromResult(store.TryGetValue(key.ToString(), out var v) ? (RedisValue)v : RedisValue.Null);
                }
            });
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        var limiter = new RedisLoginRateLimiter(mux.Object, MockTenantAccessor.CreateWithTenant(Guid.NewGuid(), "t"));
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => limiter.RegisterFailedAttemptAsync(http, "alice")));

        Assert.IsTrue(await limiter.IsLockedOutAsync(http, "alice"));
        StringAssert.Contains(script, "INCR");
        StringAssert.Contains(script, "PEXPIRE");
        Assert.AreEqual((long)TimeSpan.FromMinutes(5).TotalMilliseconds, (long)args![0]);
        // Strict mock: no StringSet, i.e. no read-modify-write of the counter.
    }

    [TestMethod]
    public void Registration_UsesRedisLimiter_WhenRedisIsConfigured()
    {
        var withRedis = BuildLimiter(services => services.AddSingleton(Mock.Of<IConnectionMultiplexer>()));
        var withoutRedis = BuildLimiter(_ => { });

        Assert.IsInstanceOfType<RedisLoginRateLimiter>(withRedis);
        Assert.IsInstanceOfType<DistributedLoginRateLimiter>(withoutRedis);
    }

    private static ILoginRateLimiter BuildLimiter(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantAccessor>(MockTenantAccessor.CreateWithTenant(Guid.NewGuid(), "t"));
        services.AddSingleton<IDistributedCache>(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        configure(services);
        services.AddScoped(LoginRateLimit.Create);
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ILoginRateLimiter>();
    }

    [TestMethod]
    public async Task MemoryFallback_ConcurrentFailures_ReachTheThreshold()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new DistributedLoginRateLimiter(cache, MockTenantAccessor.CreateWithTenant(Guid.NewGuid(), "t"));
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
