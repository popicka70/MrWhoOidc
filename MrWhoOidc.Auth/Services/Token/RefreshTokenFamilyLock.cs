using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Serializes rotation and revocation of one refresh token family on PostgreSQL.
/// </summary>
/// <remarks>
/// Rotation claims the parent and inserts the child in one transaction that holds this lock; a family revocation
/// takes the same lock before its UPDATE. Under READ COMMITTED the UPDATE then runs on a snapshot taken after any
/// concurrent rotation committed, so the child that rotation inserted is revoked too. Without the lock a revocation
/// could miss a child that was still uncommitted when its statement started. Other providers (tests) do nothing.
/// The lock is transaction scoped, so callers must hold a transaction.
/// </remarks>
internal static class RefreshTokenFamilyLock
{
    public static async Task AcquireAsync(AuthDbContext db, Guid familyId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(@p0)", [ComputeLockKey(familyId)], ct).ConfigureAwait(false);
    }

    internal static long ComputeLockKey(Guid familyId)
    {
        // Namespaced so a key cannot collide with the signing-key locks (KeyStore.ComputeAdvisoryLockKey).
        var prefix = "refresh-family"u8;
        var input = new byte[prefix.Length + 1 + 16];
        prefix.CopyTo(input);
        input[prefix.Length] = 0x1F;
        familyId.TryWriteBytes(input.AsSpan(prefix.Length + 1));
        return BitConverter.ToInt64(SHA256.HashData(input), 0);
    }
}
