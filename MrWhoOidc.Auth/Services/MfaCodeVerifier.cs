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
}

internal sealed class MfaCodeVerifier(
    AuthDbContext db,
    ITotpService totp,
    ISecretProtector? secretProtector = null,
    ILogger<MfaCodeVerifier>? logger = null) : IMfaCodeVerifier
{
    public async Task<bool> VerifyTotpAsync(Guid accountId, string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var state = await db.UserAccounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => new { a.TotpSecret, a.TotpLastUsedStep })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (state is null || string.IsNullOrWhiteSpace(state.TotpSecret))
        {
            return false;
        }

        var secret = secretProtector?.UnprotectTotpSecret(state.TotpSecret) ?? state.TotpSecret;
        var step = totp.FindMatchingStep(secret!, code.Trim(), "SHA256");
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
}
