using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Process-wide bound on concurrent Argon2id operations.
/// <para>
/// Every Argon2id hash/verify allocates 128 MiB. Unbounded, a burst of logins (many distinct
/// usernames, so the per-tenant/IP/username limiter never triggers) or client-secret checks can
/// exhaust a small container. The bound is shared by password and client-secret verification so a
/// burst of one kind cannot starve the other, and it is derived from the process's real memory
/// limit (cgroup or GC hard limit) rather than CPU count alone.
/// </para>
/// <para>
/// When no slot is available the operation is refused: verification fails closed and hashing throws.
/// Configure an explicit bound with <c>Auth:Argon2MaxConcurrentVerifications</c> or the
/// <c>MRWHOIDC_ARGON2_MAX_CONCURRENT</c> environment variable.
/// </para>
/// </summary>
internal static class Argon2VerificationGate
{
    private const long Argon2MemoryBytes = 128L * 1024 * 1024;
    private const long FallbackMemoryBytes = 512L * 1024 * 1024;

    /// <summary>How long a caller waits for a slot before the operation is refused.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Lazy<int> MaxConcurrent = new(
        ResolveMaxConcurrent,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<SemaphoreSlim> Slots = new(
        () => new SemaphoreSlim(MaxConcurrent.Value, MaxConcurrent.Value),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static int? _configuredMax;

    public static int CurrentLimit => MaxConcurrent.Value;

    /// <summary>Sets an explicit limit before first use. Invalid values are ignored.</summary>
    public static void Configure(int? maxConcurrent)
    {
        if (maxConcurrent is > 0)
        {
            _configuredMax = Math.Min(maxConcurrent.Value, 16);
        }
    }

    /// <summary>
    /// Runs an Argon2 verification while holding a slot. Returns false when no slot becomes
    /// available in time (the caller must treat that as a failed verification).
    /// </summary>
    public static bool TryVerify(ILogger? logger, string reason, Func<bool> verify)
        => Run(logger, reason, verify, fallback: false);

    /// <summary>
    /// Runs an Argon2 hash while holding a slot. Throws when no slot becomes available in time:
    /// returning an empty hash would silently store invalid credential material.
    /// </summary>
    public static string Hash(ILogger? logger, string reason, Func<string> hash)
    {
        if (!Slots.Value.Wait(WaitTimeout))
        {
            logger?.LogWarning(
                "Argon2 operation refused: verification capacity exhausted (reason {Reason}, limit {Limit})",
                reason,
                MaxConcurrent.Value);
            throw new InvalidOperationException("Argon2 operation capacity exhausted; retry later.");
        }

        try
        {
            return hash();
        }
        finally
        {
            Slots.Value.Release();
        }
    }

    private static bool Run(ILogger? logger, string reason, Func<bool> action, bool fallback)
    {
        if (!Slots.Value.Wait(WaitTimeout))
        {
            logger?.LogWarning(
                "Argon2 operation refused: verification capacity exhausted (reason {Reason}, limit {Limit})",
                reason,
                MaxConcurrent.Value);
            return fallback;
        }

        try
        {
            return action();
        }
        finally
        {
            Slots.Value.Release();
        }
    }

    private static int ResolveMaxConcurrent()
    {
        if (_configuredMax is > 0)
        {
            return _configuredMax.Value;
        }

        var envValue = Environment.GetEnvironmentVariable("MRWHOIDC_ARGON2_MAX_CONCURRENT");
        if (int.TryParse(envValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnv)
            && fromEnv > 0)
        {
            return Math.Min(fromEnv, 16);
        }

        var availableBytes = TryReadCgroupMemoryLimitBytes()
            ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableBytes <= 0)
        {
            availableBytes = FallbackMemoryBytes;
        }

        // Argon2 allocations are native, not GC-heap memory, so reserve a generous fraction of
        // the container for the runtime, request state and buffers. On a 512 MiB instance this
        // yields one concurrent verification; on a 2 GiB instance the CPU cap applies.
        var memoryBudget = (long)(availableBytes * 0.40);
        var byMemory = (int)(memoryBudget / Argon2MemoryBytes);
        var byCpu = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        return Math.Clamp(Math.Min(byCpu, Math.Max(1, byMemory)), 1, 4);
    }

    private static long? TryReadCgroupMemoryLimitBytes()
    {
        string[] paths =
        [
            "/sys/fs/cgroup/memory.max",
            "/sys/fs/cgroup/memory/memory.limit_in_bytes"
        ];

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var text = File.ReadAllText(path).Trim();
                if (text.Equals("max", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
                    && bytes > 0
                    && bytes < (1L << 60)) // v1 uses a huge sentinel for "unlimited"
                {
                    return bytes;
                }
            }
            catch (IOException)
            {
                // Best effort: fall back to the GC-visible limit.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        return null;
    }
}
