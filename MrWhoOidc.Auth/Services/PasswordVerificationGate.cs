using Microsoft.Extensions.Logging;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// M-Argon2: a process-wide bound on concurrent user-login password verifications.
/// <para>
/// A user login performs an Argon2id verification with 128 MiB of memory per verification, and the login
/// path also performs a dummy verification for unknown users (to equalize the timing). Client-secret
/// verification already has a process-wide cap (see <c>ClientStore</c>); without an equivalent bound here a
/// burst of login attempts - spraying many distinct usernames, so the per-tenant/IP/username limiter never
/// triggers - could exhaust the memory of the host.
/// </para>
/// <para>
/// The bound is deliberately separate from the client-secret bound, so that a burst of one kind of
/// verification cannot starve the other. When no slot is available the verification is refused (fail
/// closed): the login fails instead of the host running out of memory.
/// </para>
/// </summary>
internal static class PasswordVerificationGate
{
    /// <summary>Concurrent verifications allowed process-wide.</summary>
    private static readonly SemaphoreSlim Slots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    /// <summary>How long a caller waits for a verification slot before the attempt is refused.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs a password verification while holding a verification slot.
    /// Returns false (and logs) when no slot becomes available in time.
    /// </summary>
    internal static bool Verify(IPasswordHasher hasher, string password, string hash, ILogger logger, string reason)
        => Run(logger, reason, () => hasher.Verify(password, hash));

    /// <summary>
    /// Runs an expensive password-hashing action while holding a verification slot.
    /// Returns the default value of <typeparamref name="T"/> when no slot becomes available in time.
    /// </summary>
    internal static T Run<T>(ILogger logger, string reason, Func<T> action)
    {
        if (!Slots.Wait(WaitTimeout))
        {
            logger.LogWarning(
                "Password verification refused: verification capacity exhausted (reason {Reason})",
                reason);
            return default!;
        }

        try
        {
            return action();
        }
        finally
        {
            Slots.Release();
        }
    }
}
