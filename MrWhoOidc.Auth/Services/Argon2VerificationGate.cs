using System.Globalization;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Process-wide pool of reusable Argon2id working buffers.
/// <para>
/// Argon2id at the server profile used by this OP (m=131072 KiB, t=4, p=4) needs a ~128 MiB
/// working area per operation. Allocating that area fresh for every login or secret check —
/// what the released Isopoh 2.0.0 package does, as a pinned managed <see cref="byte"/>[] on
/// the Large Object Heap — caused repeated transient heap spikes and cgroup OOM kills on the
/// 512 MiB deployment. This gate instead owns a bounded pool of <see cref="Argon2Memory"/>
/// buffers from the vendored upstream sources and rents one exclusively per operation, so
/// sequential logins reuse the same memory instead of churning the heap.
/// </para>
/// <para>
/// The bound is shared by password and client-secret verification so a burst of one kind
/// cannot starve the other, and it is derived from the process's real memory limit (cgroup
/// or GC hard limit) rather than CPU count alone. Each retained buffer is
/// <see cref="Argon2MemoryPolicy.NoShrink"/> (it never shrinks below the configured 128 MiB
/// even when verifying cheaper hashes) and lazily allocated on first rent, so an idle
/// process holds no Argon2 memory. Buffers are retained for the process lifetime and
/// zeroed by the library when disposed at shutdown.
/// </para>
/// <para>
/// When no buffer becomes available within the wait window the operation is refused:
/// verification fails closed and hashing throws. Configure an explicit bound with
/// <c>Auth:Argon2MaxConcurrentVerifications</c> or the <c>MRWHOIDC_ARGON2_MAX_CONCURRENT</c>
/// environment variable (the count is also the number of retained buffers).
/// </para>
/// </summary>
internal static class Argon2VerificationGate
{
    private const long Argon2MemoryBytes = 128L * 1024 * 1024;
    private const long FallbackMemoryBytes = 512L * 1024 * 1024;

    /// <summary>How long a caller waits for a buffer before the operation is refused.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Lazy<int> MaxConcurrent = new(
        ResolveMaxConcurrent,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Guards <see cref="Idle"/>, <see cref="Created"/> and the rent/return handshake.</summary>
    private static readonly object Sync = new();

    /// <summary>Idle, previously-allocated buffers available for immediate rent.</summary>
    private static readonly Queue<Argon2Memory> Idle = new();

    /// <summary>Total buffers created so far (each retained for the process lifetime).</summary>
    private static int Created;

    private static int? _configuredMax;

    public static int CurrentLimit => MaxConcurrent.Value;

    /// <summary>Buffers allocated so far (diagnostics/tests).</summary>
    public static int AllocatedBufferCount
    {
        get
        {
            lock (Sync)
            {
                return Created;
            }
        }
    }

    /// <summary>Sets an explicit limit before first use. Invalid values are ignored.</summary>
    public static void Configure(int? maxConcurrent)
    {
        if (maxConcurrent is > 0)
        {
            _configuredMax = Math.Min(maxConcurrent.Value, 16);
        }
    }

    /// <summary>
    /// Runs an Argon2 verification while holding a pooled buffer. Returns false when no buffer
    /// becomes available in time (the caller must treat that as a failed verification).
    /// </summary>
    public static bool TryVerify(ILogger? logger, string reason, Func<Argon2Memory, bool> verify)
        => Run(logger, reason, verify, fallback: false);

    /// <summary>
    /// Runs an Argon2 hash while holding a pooled buffer. Throws when no buffer becomes
    /// available in time: returning an empty hash would silently store invalid credential
    /// material.
    /// </summary>
    public static string Hash(ILogger? logger, string reason, Func<Argon2Memory, string> hash)
    {
        var memory = Rent(logger, reason) ?? throw new InvalidOperationException(
            "Argon2 operation capacity exhausted; retry later.");

        try
        {
            return hash(memory);
        }
        finally
        {
            Return(memory);
        }
    }

    private static bool Run(ILogger? logger, string reason, Func<Argon2Memory, bool> action, bool fallback)
    {
        var memory = Rent(logger, reason);
        if (memory is null)
        {
            return fallback;
        }

        try
        {
            return action(memory);
        }
        finally
        {
            Return(memory);
        }
    }

    /// <summary>
    /// Rent an exclusive buffer, creating one when the pool is below its bound. Returns null
    /// when the bound is reached and no buffer is returned within <see cref="WaitTimeout"/>.
    /// </summary>
    private static Argon2Memory? Rent(ILogger? logger, string reason)
    {
        lock (Sync)
        {
            var deadline = Environment.TickCount64 + (long)WaitTimeout.TotalMilliseconds;
            while (true)
            {
                if (Idle.Count > 0)
                {
                    return Idle.Dequeue();
                }

                if (Created < MaxConcurrent.Value)
                {
                    Created++;
                    try
                    {
                        return CreateBuffer();
                    }
                    catch
                    {
                        // Allocation failure (OOM / lock-policy hard failure) frees the slot
                        // for the next attempt; the exception propagates to the caller.
                        Created--;
                        throw;
                    }
                }

                var remainingMs = deadline - Environment.TickCount64;
                if (remainingMs <= 0)
                {
                    logger?.LogWarning(
                        "Argon2 operation refused: verification capacity exhausted (reason {Reason}, limit {Limit})",
                        reason,
                        MaxConcurrent.Value);
                    return null;
                }

                _ = Monitor.Wait(Sync, (int)remainingMs);
            }
        }
    }

    private static void Return(Argon2Memory memory)
    {
        lock (Sync)
        {
            Idle.Enqueue(memory);
            Monitor.Pulse(Sync);
        }
    }

    /// <summary>
    /// A buffer sized for the server profile used by every hash this OP creates. Stored
    /// hashes embed their own parameters, and <see cref="Argon2Memory.Reset(string, Argon2Password)"/>
    /// grows the buffer if it ever sees a larger requirement.
    /// </summary>
    private static Argon2Memory CreateBuffer()
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = 4,
            MemoryCost = 131072,
            Lanes = 4,
            Threads = 1,
            HashLength = 256 / 8,
        };

        return new Argon2Memory(config, Argon2MemoryPolicy.NoShrink, LockMemoryPolicy.BestEffort);
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

        // The buffers are pinned managed arrays on the Large Object Heap, so the count must
        // respect both the GC hard limit and the cgroup limit: leave the majority of the
        // container for the runtime, request state and heap. On a 512 MiB instance this
        // yields a single buffer; on larger instances the CPU cap applies.
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
