using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Infrastructure.DistributedState;

namespace MrWhoOidc.UnitTests.Infrastructure;

/// <summary>
/// Verifies the database-backed distributed state store against SQLite, the relational provider
/// used by the test suite. The PostgreSQL path shares the same ON CONFLICT semantics.
/// </summary>
[TestClass]
public sealed class DatabaseDistributedStateStoreTests
{
    private string _dbPath = null!;
    private DbContextOptions<AuthDbContext> _options = null!;
    private DatabaseDistributedStateStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mrwhooidc-state-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false,
            DefaultTimeout = 60
        }.ToString();

        _options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using (var db = new AuthDbContext(_options))
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        _store = new DatabaseDistributedStateStore(
            new TestDbContextFactory(_options),
            NullLogger<DatabaseDistributedStateStore>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [TestMethod]
    public async Task TryAdd_AllowsExactlyOneConcurrentWinner()
    {
        const int contenders = 16;
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, contenders)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await _store.TryAddAsync("dpop:replay:jkt:jti", "1", TimeSpan.FromMinutes(5));
            }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.AreEqual(1, results.Count(won => won), "SET-if-absent must be atomic across contexts.");
        Assert.AreEqual("1", await _store.GetAsync("dpop:replay:jkt:jti"));
    }

    [TestMethod]
    public async Task Set_Get_Refresh_Remove_RoundTrip()
    {
        var key = "test:roundtrip";

        await _store.SetAsync(key, "v1", TimeSpan.FromMinutes(5));
        Assert.AreEqual("v1", await _store.GetAsync(key));
        Assert.IsNotNull(await _store.GetTimeToLiveAsync(key));

        Assert.IsTrue(await _store.RefreshAsync(key, TimeSpan.FromMinutes(10)));
        var ttl = await _store.GetTimeToLiveAsync(key);
        Assert.IsNotNull(ttl);
        Assert.IsTrue(ttl > TimeSpan.FromMinutes(9));

        Assert.IsTrue(await _store.RemoveAsync(key));
        Assert.IsNull(await _store.GetAsync(key));
        Assert.IsFalse(await _store.RemoveAsync(key));
        Assert.IsFalse(await _store.RefreshAsync(key, TimeSpan.FromMinutes(1)));
    }

    [TestMethod]
    public async Task Get_ReturnsNull_WhenEntryExpired()
    {
        await _store.SetAsync("test:ttl", "value", TimeSpan.FromMilliseconds(200));

        Assert.AreEqual("value", await _store.GetAsync("test:ttl"));

        await Task.Delay(500);

        Assert.IsNull(await _store.GetAsync("test:ttl"));
        Assert.IsNull(await _store.GetTimeToLiveAsync("test:ttl"));
    }

    [TestMethod]
    public async Task Increment_IsAtomic_AndPreservesFirstTtl()
    {
        const int increments = 32;
        var key = "rl:policy:bucket";

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, increments)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                return await _store.IncrementAsync(key, TimeSpan.FromMinutes(5));
            }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        CollectionAssert.AreEquivalent(
            Enumerable.Range(1, increments).Select(i => (long)i).ToArray(),
            results,
            "Every concurrent increment must observe a unique count.");
        Assert.AreEqual(increments.ToString(), await _store.GetAsync(key));
        Assert.IsNotNull(await _store.GetTimeToLiveAsync(key), "The first increment must set the window expiry.");
    }

    [TestMethod]
    public async Task Increment_RestartsAtOne_WhenPreviousWindowExpired()
    {
        await _store.SetAsync("rl:expired", "99", TimeSpan.FromMilliseconds(150));
        await Task.Delay(350);

        Assert.AreEqual(1, await _store.IncrementAsync("rl:expired", TimeSpan.FromMinutes(1)));
        Assert.AreEqual(2, await _store.IncrementAsync("rl:expired", TimeSpan.FromMinutes(1)));
    }

    [TestMethod]
    public async Task DeleteExpired_RemovesOnlyExpiredRows()
    {
        await _store.SetAsync("test:live", "1", TimeSpan.FromMinutes(5));
        await _store.SetAsync("test:expired", "1", TimeSpan.FromMilliseconds(150));
        await Task.Delay(350);

        var removed = await _store.DeleteExpiredAsync(100);

        Assert.AreEqual(1, removed);
        Assert.AreEqual("1", await _store.GetAsync("test:live"));
        Assert.IsNull(await _store.GetAsync("test:expired"));
    }

    [TestMethod]
    public async Task OversizeKeys_AreNormalizedDeterministically()
    {
        // Replay keys embed attacker-controlled identifiers such as jti. A key beyond the
        // column's 512-character bound must still round-trip instead of failing the request.
        var key = "jar:replay:client:" + new string('j', 2000);

        await _store.SetAsync(key, "1", TimeSpan.FromMinutes(5));

        Assert.AreEqual("1", await _store.GetAsync(key));
        Assert.IsNull(await _store.GetAsync(key + "-different"));
        Assert.IsTrue(await _store.RemoveAsync(key));
        Assert.IsNull(await _store.GetAsync(key));
    }

    private sealed class TestDbContextFactory(DbContextOptions<AuthDbContext> options) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(options);

        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
