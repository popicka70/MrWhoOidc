using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Idempotent startup backfill: protects signing/encryption keys (<see cref="SigningKey.JwkJson"/>) and TOTP secrets
/// (<see cref="UserAccount.TotpSecret"/>, <see cref="User.TotpSecret"/>) still stored in plaintext. Every current
/// write path protects on save (<see cref="AuthDbContext"/>), so only rows written before that exist in plaintext.
/// After it succeeds the host marks <see cref="PlaintextSecretPolicy"/> complete and, with
/// <c>Security:RejectPlaintextSecrets</c>, reading a plaintext value fails from then on.
/// </summary>
public static class StoredSecretProtectionBackfill
{
    public static async Task<int> RunAsync(AuthDbContext db, ISecretProtector protector, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protector);

        // Protect explicitly (Protect is a no-op on dp:v1: values) rather than relying on the context having a protector.
        var signingKeys = await db.SigningKeys.IgnoreQueryFilters()
            .Where(k => k.JwkJson != "" && !k.JwkJson.StartsWith("dp:v1:"))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var key in signingKeys)
        {
            key.JwkJson = protector.ProtectSigningKeyJwk(key.JwkJson);
        }

        var accounts = await db.UserAccounts.IgnoreQueryFilters()
            .Where(a => a.TotpSecret != null && a.TotpSecret != "" && !a.TotpSecret.StartsWith("dp:v1:"))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var account in accounts)
        {
            account.TotpSecret = protector.ProtectTotpSecret(account.TotpSecret!);
        }

        var users = await db.Users.IgnoreQueryFilters()
            .Where(u => u.TotpSecret != null && u.TotpSecret != "" && !u.TotpSecret.StartsWith("dp:v1:"))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var user in users)
        {
            user.TotpSecret = protector.ProtectTotpSecret(user.TotpSecret!);
        }

        var total = signingKeys.Count + accounts.Count + users.Count;
        if (total == 0)
        {
            return 0;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Protected plaintext secrets at rest: {SigningKeys} signing key(s), {Accounts} account TOTP secret(s), {Users} user TOTP secret(s)",
            signingKeys.Count, accounts.Count, users.Count);
        return total;
    }
}
