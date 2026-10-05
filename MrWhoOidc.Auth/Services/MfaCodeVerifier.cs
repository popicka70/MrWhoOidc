using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Verifies second-factor codes for a global <see cref="UserAccount"/> and consumes them, so that a code that
/// was accepted once (or observed by an attacker) cannot be accepted again.
/// </summary>
public interface IMfaCodeVerifier
{
    /// <summary>
    /// Verifies <paramref name="code"/> against the account's TOTP secret (confirmed or pending enrolment) and,
    /// when it matches, records its time step. A code whose step is not newer than the last accepted one is
    /// refused (RFC 6238 §5.2). The step is recorded atomically, so two concurrent requests with the same code
    /// cannot both succeed.
    /// </summary>
    Task<bool> VerifyTotpAsync(Guid accountId, string? code, CancellationToken ct = default);

    /// <summary>
    /// Replaces the account's recovery codes with <see cref="MfaCodeVerifier.RecoveryCodeCount"/> new ones and
    /// returns them in display form. Only hashes are stored, so this is the only time they can be shown.
    /// </summary>
    Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Redeems an unused recovery code; each code succeeds at most once, even under concurrency.</summary>
    Task<bool> ConsumeRecoveryCodeAsync(Guid accountId, string? code, CancellationToken ct = default);

    /// <summary>Number of recovery codes the account can still redeem.</summary>
    Task<int> CountUnusedRecoveryCodesAsync(Guid accountId, CancellationToken ct = default);
}

internal sealed class MfaCodeVerifier(
    AuthDbContext db,
    ITotpService totp,
    ISecretProtector? secretProtector = null,
    ILogger<MfaCodeVerifier>? logger = null) : IMfaCodeVerifier
{
    public const int RecoveryCodeCount = 10;

    // 16 base32 characters = 80 bits per code: far beyond online guessing, and enough that a leaked SHA-256 hash
    // (salted with the account id) cannot be reversed by brute force.
    private const int RecoveryCodeLength = 16;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public async Task<bool> VerifyTotpAsync(Guid accountId, string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var state = await db.UserAccounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => new { a.TotpSecret, a.TotpAlgorithm, a.TotpLastUsedStep })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (state is null || string.IsNullOrWhiteSpace(state.TotpSecret))
        {
            return false;
        }

        var secret = secretProtector?.UnprotectTotpSecret(state.TotpSecret) ?? state.TotpSecret;
        var step = totp.FindMatchingStep(secret!, code.Trim(), TotpAlgorithms.Resolve(state.TotpAlgorithm));
        if (step is null)
        {
            return false;
        }

        if (state.TotpLastUsedStep is { } last && step.Value <= last)
        {
            logger?.LogWarning("TOTP code refused for UserAccount {AccountId}: time step already used", accountId);
            return false;
        }

        if (!await TryAdvanceStepAsync(accountId, step.Value, ct).ConfigureAwait(false))
        {
            logger?.LogWarning("TOTP code refused for UserAccount {AccountId}: time step consumed concurrently", accountId);
            return false;
        }

        return true;
    }

    /// <summary>Moves TotpLastUsedStep forward to <paramref name="step"/> only if it is still older.</summary>
    private async Task<bool> TryAdvanceStepAsync(Guid accountId, long step, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var rows = await db.UserAccounts
                .Where(a => a.Id == accountId && (a.TotpLastUsedStep == null || a.TotpLastUsedStep < step))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TotpLastUsedStep, step), ct)
                .ConfigureAwait(false);
            if (rows == 1)
            {
                // Keep an already tracked instance in step with the row, so a later SaveChanges cannot regress it.
                var tracked = db.UserAccounts.Local.FirstOrDefault(a => a.Id == accountId);
                if (tracked is not null)
                {
                    tracked.TotpLastUsedStep = step;
                    db.Entry(tracked).Property(a => a.TotpLastUsedStep).IsModified = false;
                }
            }
            return rows == 1;
        }

        // Non-relational providers (tests) have no conditional update; a tracked check-and-set is the best available.
        var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false);
        if (account is null || (account.TotpLastUsedStep is { } current && current >= step))
        {
            return false;
        }

        account.TotpLastUsedStep = step;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid accountId, CancellationToken ct = default)
    {
        var existing = await db.UserAccountRecoveryCodes
            .Where(c => c.UserAccountId == accountId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        db.UserAccountRecoveryCodes.RemoveRange(existing);

        var codes = new List<string>(RecoveryCodeCount);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = RandomNumberGenerator.GetString(Base32Alphabet, RecoveryCodeLength);
            codes.Add(string.Join('-', raw.Chunk(4).Select(chunk => new string(chunk))));
            db.UserAccountRecoveryCodes.Add(new UserAccountRecoveryCode
            {
                UserAccountId = accountId,
                CodeHash = HashRecoveryCode(accountId, raw),
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger?.LogInformation("Issued {Count} MFA recovery codes for UserAccount {AccountId}", RecoveryCodeCount, accountId);
        return codes;
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(Guid accountId, string? code, CancellationToken ct = default)
    {
        var normalized = NormalizeRecoveryCode(code);
        if (normalized is null)
        {
            return false;
        }

        var hash = HashRecoveryCode(accountId, normalized);
        var now = DateTimeOffset.UtcNow;
        bool consumed;
        if (db.Database.IsRelational())
        {
            // Conditional on UsedAt still being null: of two concurrent redemptions only one updates the row.
            consumed = await db.UserAccountRecoveryCodes
                .Where(c => c.UserAccountId == accountId && c.CodeHash == hash && c.UsedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, now), ct)
                .ConfigureAwait(false) == 1;
        }
        else
        {
            var row = await db.UserAccountRecoveryCodes
                .FirstOrDefaultAsync(c => c.UserAccountId == accountId && c.CodeHash == hash && c.UsedAt == null, ct)
                .ConfigureAwait(false);
            if (row is not null)
            {
                row.UsedAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            consumed = row is not null;
        }

        if (consumed)
        {
            logger?.LogInformation("MFA recovery code redeemed for UserAccount {AccountId}", accountId);
        }
        return consumed;
    }

    public Task<int> CountUnusedRecoveryCodesAsync(Guid accountId, CancellationToken ct = default)
        => db.UserAccountRecoveryCodes.CountAsync(c => c.UserAccountId == accountId && c.UsedAt == null, ct);

    /// <summary>
    /// Upper-cases and strips separators/whitespace; returns null unless the result has the shape of a recovery
    /// code, so a mistyped TOTP code is never hashed and looked up as one.
    /// </summary>
    internal static string? NormalizeRecoveryCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var builder = new StringBuilder(RecoveryCodeLength);
        foreach (var c in code)
        {
            if (c == '-' || char.IsWhiteSpace(c))
            {
                continue;
            }
            builder.Append(char.ToUpperInvariant(c));
        }

        var normalized = builder.ToString();
        return normalized.Length == RecoveryCodeLength && normalized.All(c => Base32Alphabet.Contains(c))
            ? normalized
            : null;
    }

    private static string HashRecoveryCode(Guid accountId, string normalizedCode)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId:N}:{normalizedCode}")));
}
