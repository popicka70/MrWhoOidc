using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>
/// Database-backed <see cref="IDistributedStateStore"/> over the <c>DistributedStateEntries</c> table.
/// <para>
/// Relational providers use atomic <c>INSERT ... ON CONFLICT</c> statements for set-if-absent and
/// counter increments, so multiple replicas sharing one database see the same replay/rate-limit
/// state. The EF in-memory provider (tests) uses a process-local striped lock instead, which is
/// exactly the single-process semantics that provider can offer.
/// </para>
/// </summary>
public sealed class DatabaseDistributedStateStore(
    IDbContextFactory<AuthDbContext> dbFactory,
    ILogger<DatabaseDistributedStateStore> logger) : IDistributedStateStore
{
    private enum StoreKind { PostgreSql, Sqlite, InMemory, Unknown }

    private static readonly SemaphoreSlim[] MemoryStripes =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private StoreKind _kind = StoreKind.Unknown;

    public async Task<bool> TryAddAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var expires = now.Add(NormalizeTtl(ttl));

        switch (ResolveKind(db))
        {
            case StoreKind.PostgreSql:
            case StoreKind.Sqlite:
            {
                // The WHERE clause only lets the update through when the previous row has expired.
                // If the row is live the statement affects zero rows and the caller lost the race.
                var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "DistributedStateEntries" ("Key", "Value", "ExpiresAtUtc", "CreatedAtUtc")
                    VALUES ({key}, {value}, {expires}, {now})
                    ON CONFLICT ("Key") DO UPDATE
                    SET "Value" = EXCLUDED."Value",
                        "ExpiresAtUtc" = EXCLUDED."ExpiresAtUtc",
                        "CreatedAtUtc" = EXCLUDED."CreatedAtUtc"
                    WHERE "DistributedStateEntries"."ExpiresAtUtc" <= {now}
                    """, ct).ConfigureAwait(false);
                return affected > 0;
            }
            case StoreKind.InMemory:
            {
                var stripe = StripeFor(key);
                await stripe.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var entry = await db.DistributedStateEntries
                        .FirstOrDefaultAsync(e => e.Key == key, ct).ConfigureAwait(false);
                    if (entry is not null && entry.ExpiresAtUtc > now)
                    {
                        return false;
                    }

                    if (entry is null)
                    {
                        db.DistributedStateEntries.Add(new DistributedStateEntry
                        {
                            Key = key,
                            Value = value,
                            ExpiresAtUtc = expires,
                            CreatedAtUtc = now
                        });
                    }
                    else
                    {
                        entry.Value = value;
                        entry.ExpiresAtUtc = expires;
                        entry.CreatedAtUtc = now;
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    stripe.Release();
                }
            }
            default:
                throw UnsupportedProvider(db);
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var entry = await db.DistributedStateEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key, ct).ConfigureAwait(false);
        return entry is null || entry.ExpiresAtUtc <= now ? null : entry.Value;
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var expires = now.Add(NormalizeTtl(ttl));

        switch (ResolveKind(db))
        {
            case StoreKind.PostgreSql:
            case StoreKind.Sqlite:
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "DistributedStateEntries" ("Key", "Value", "ExpiresAtUtc", "CreatedAtUtc")
                    VALUES ({key}, {value}, {expires}, {now})
                    ON CONFLICT ("Key") DO UPDATE
                    SET "Value" = EXCLUDED."Value",
                        "ExpiresAtUtc" = EXCLUDED."ExpiresAtUtc",
                        "CreatedAtUtc" = EXCLUDED."CreatedAtUtc"
                    """, ct).ConfigureAwait(false);
                break;

            case StoreKind.InMemory:
            {
                var stripe = StripeFor(key);
                await stripe.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var entry = await db.DistributedStateEntries
                        .FirstOrDefaultAsync(e => e.Key == key, ct).ConfigureAwait(false);
                    if (entry is null)
                    {
                        db.DistributedStateEntries.Add(new DistributedStateEntry
                        {
                            Key = key,
                            Value = value,
                            ExpiresAtUtc = expires,
                            CreatedAtUtc = now
                        });
                    }
                    else
                    {
                        entry.Value = value;
                        entry.ExpiresAtUtc = expires;
                        entry.CreatedAtUtc = now;
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    stripe.Release();
                }
                break;
            }
            default:
                throw UnsupportedProvider(db);
        }
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        switch (ResolveKind(db))
        {
            case StoreKind.PostgreSql:
            case StoreKind.Sqlite:
                return await db.DistributedStateEntries
                    .Where(e => e.Key == key)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

            case StoreKind.InMemory:
            {
                var stripe = StripeFor(key);
                await stripe.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var entry = await db.DistributedStateEntries
                        .FirstOrDefaultAsync(e => e.Key == key, ct).ConfigureAwait(false);
                    if (entry is null)
                    {
                        return false;
                    }

                    db.DistributedStateEntries.Remove(entry);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    stripe.Release();
                }
            }
            default:
                throw UnsupportedProvider(db);
        }
    }

    public async Task<bool> RefreshAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var expires = now.Add(NormalizeTtl(ttl));

        switch (ResolveKind(db))
        {
            case StoreKind.PostgreSql:
            case StoreKind.Sqlite:
                return await db.DistributedStateEntries
                    .Where(e => e.Key == key && e.ExpiresAtUtc > now)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ExpiresAtUtc, expires), ct)
                    .ConfigureAwait(false) > 0;

            case StoreKind.InMemory:
            {
                var stripe = StripeFor(key);
                await stripe.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var entry = await db.DistributedStateEntries
                        .FirstOrDefaultAsync(e => e.Key == key, ct).ConfigureAwait(false);
                    if (entry is null || entry.ExpiresAtUtc <= now)
                    {
                        return false;
                    }

                    entry.ExpiresAtUtc = expires;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    stripe.Release();
                }
            }
            default:
                throw UnsupportedProvider(db);
        }
    }

    public async Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var expires = now.Add(NormalizeTtl(ttl));

        switch (ResolveKind(db))
        {
            case StoreKind.PostgreSql:
                return await ExecuteIncrementAsync(db, """
                    INSERT INTO "DistributedStateEntries" ("Key", "Value", "ExpiresAtUtc", "CreatedAtUtc")
                    VALUES ({0}, {1}, {2}, {3})
                    ON CONFLICT ("Key") DO UPDATE
                    SET "Value" = CASE
                            WHEN "DistributedStateEntries"."ExpiresAtUtc" <= {3} THEN {1}
                            ELSE (CAST("DistributedStateEntries"."Value" AS bigint) + 1)::text
                        END,
                        "ExpiresAtUtc" = CASE
                            WHEN "DistributedStateEntries"."ExpiresAtUtc" <= {3} THEN {2}
                            ELSE "DistributedStateEntries"."ExpiresAtUtc"
                        END,
                        "CreatedAtUtc" = CASE
                            WHEN "DistributedStateEntries"."ExpiresAtUtc" <= {3} THEN {3}
                            ELSE "DistributedStateEntries"."CreatedAtUtc"
                        END
                    RETURNING CAST("Value" AS bigint) AS "Value"
                    """, key, "1", expires, now, ct).ConfigureAwait(false);

            case StoreKind.Sqlite:
                return await ExecuteIncrementAsync(db, """
                    INSERT INTO "DistributedStateEntries" ("Key", "Value", "ExpiresAtUtc", "CreatedAtUtc")
                    VALUES ({0}, {1}, {2}, {3})
                    ON CONFLICT ("Key") DO UPDATE
                    SET "Value" = CASE
                            WHEN "DistributedStateEntries"."ExpiresAtUtc" <= {3} THEN {1}
                            ELSE CAST(CAST("DistributedStateEntries"."Value" AS INTEGER) + 1 AS TEXT)
                        END,
                        "ExpiresAtUtc" = CASE
                            WHEN "DistributedStateEntries"."ExpiresAtUtc" <= {3} THEN {2}
                            ELSE "DistributedStateEntries"."ExpiresAtUtc"
                        END,
                        "CreatedAtUtc" = CASE
                            WHEN "DistributedStateEntries"."ExpiresAtUtc" <= {3} THEN {3}
                            ELSE "DistributedStateEntries"."CreatedAtUtc"
                        END
                    RETURNING CAST("Value" AS INTEGER) AS "Value"
                    """, key, "1", expires, now, ct).ConfigureAwait(false);

            case StoreKind.InMemory:
            {
                var stripe = StripeFor(key);
                await stripe.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var entry = await db.DistributedStateEntries
                        .FirstOrDefaultAsync(e => e.Key == key, ct).ConfigureAwait(false);
                    long count;
                    if (entry is null || entry.ExpiresAtUtc <= now)
                    {
                        count = 1;
                        if (entry is null)
                        {
                            db.DistributedStateEntries.Add(new DistributedStateEntry
                            {
                                Key = key,
                                Value = "1",
                                ExpiresAtUtc = expires,
                                CreatedAtUtc = now
                            });
                        }
                        else
                        {
                            entry.Value = "1";
                            entry.ExpiresAtUtc = expires;
                            entry.CreatedAtUtc = now;
                        }
                    }
                    else
                    {
                        count = long.TryParse(entry.Value, out var existing) ? existing + 1 : 1;
                        entry.Value = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return count;
                }
                finally
                {
                    stripe.Release();
                }
            }
            default:
                throw UnsupportedProvider(db);
        }
    }

    public async Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken ct = default)
    {
        key = DistributedStateKey.Normalize(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var entry = await db.DistributedStateEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key, ct).ConfigureAwait(false);
        if (entry is null || entry.ExpiresAtUtc <= now)
        {
            return null;
        }

        return entry.ExpiresAtUtc - now;
    }

    public async Task<int> DeleteExpiredAsync(int batchSize, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        switch (ResolveKind(db))
        {
            case StoreKind.PostgreSql:
            case StoreKind.Sqlite:
                return await db.DistributedStateEntries
                    .Where(e => e.ExpiresAtUtc <= now)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            case StoreKind.InMemory:
            {
                var stripe = StripeFor("cleanup");
                await stripe.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var expired = await db.DistributedStateEntries
                        .Where(e => e.ExpiresAtUtc <= now)
                        .Take(Math.Max(1, batchSize))
                        .ToListAsync(ct).ConfigureAwait(false);
                    if (expired.Count == 0)
                    {
                        return 0;
                    }

                    db.DistributedStateEntries.RemoveRange(expired);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return expired.Count;
                }
                finally
                {
                    stripe.Release();
                }
            }
            default:
                throw UnsupportedProvider(db);
        }
    }

    private static async Task<long> ExecuteIncrementAsync(
        AuthDbContext db,
        string sql,
        string key,
        string initialValue,
        DateTime expiresAtUtc,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // SqlQueryRaw is not composable: materialize the single RETURNING row, then read it.
        var rows = await db.Database
            .SqlQueryRaw<long>(sql, key, initialValue, expiresAtUtc, nowUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Single();
    }

    private StoreKind ResolveKind(AuthDbContext db)
    {
        if (_kind != StoreKind.Unknown)
        {
            return _kind;
        }

        var providerName = db.Database.ProviderName ?? string.Empty;
        _kind = providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ? StoreKind.PostgreSql
            : providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? StoreKind.Sqlite
            : providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase) ? StoreKind.InMemory
            : StoreKind.Unknown;

        if (_kind == StoreKind.Unknown)
        {
            logger.LogError("Unsupported database provider for distributed state: {ProviderName}", providerName);
        }

        return _kind;
    }

    private static NotSupportedException UnsupportedProvider(AuthDbContext db)
        => new($"DistributedState:Provider=Database does not support the configured EF provider '{db.Database.ProviderName}'.");

    private static SemaphoreSlim StripeFor(string key)
        => MemoryStripes[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % MemoryStripes.Length];

    private static TimeSpan NormalizeTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        // Guard against accidental eternal rows (a TimeSpan.MaxValue TTL would overflow Add()).
        var max = TimeSpan.FromDays(3650);
        return ttl > max ? max : ttl;
    }
}
