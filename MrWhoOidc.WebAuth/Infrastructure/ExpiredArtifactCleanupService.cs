using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Infrastructure;

/// <summary>
/// H6: several single-use tables had no expiry sweep, so they grew without bound.
/// <para>
/// This service removes the rows that can no longer be used:
/// </para>
/// <list type="bullet">
/// <item>authorization codes (after a retention window, so reuse detection and the logout-targeting window
/// keep working),</item>
/// <item>device code entries that expired without being redeemed,</item>
/// <item>logout redirect references (single use, five minute lifetime),</item>
/// <item>email confirmations that expired,</item>
/// <item>tenant invitations that expired while still pending.</item>
/// </list>
/// <para>
/// Expiry is tenant-independent, so the sweep runs cross-tenant on purpose (the same reasoning as the
/// expired-token cleanup). Denied and redeemed device entries already have their own removal path.
/// </para>
/// </summary>
internal sealed class ExpiredArtifactCleanupService(
    IServiceProvider services,
    ILogger<ExpiredArtifactCleanupService> logger) : BackgroundService
{
    /// <summary>
    /// How long an expired authorization code is kept.
    /// <para>
    /// It must stay above the session-participation window used by the logout target resolver (12 h), so
    /// that the RPs a user logged into are still known, and above a realistic replay window, so that a
    /// replayed code is still detected as reuse (and still revokes the user's tokens for that client).
    /// </para>
    /// </summary>
    internal static readonly TimeSpan AuthorizationCodeRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an ended OP session (F1) is kept. A cookie lives at most eight hours, so a session that ended a
    /// day ago can no longer be referenced by one.
    /// </summary>
    internal static readonly TimeSpan OpSessionRetention = TimeSpan.FromHours(24);

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) { }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                await SweepAsync(db, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // graceful shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expired artifact cleanup failed");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// Removes every expired artifact. Exposed so the behaviour can be tested without the timer.
    /// </summary>
    internal static async Task<int> SweepAsync(AuthDbContext db, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Authorization codes: keep them for the retention window so that reuse detection and the
        // logout-targeting window still see them.
        var codeCutoff = now - AuthorizationCodeRetention;
        var codes = await DeleteAsync(db, db.AuthorizationCodes, c => c.ExpiresAt < codeCutoff, cancellationToken);

        // Device codes: an expired entry can never be redeemed. Denied/redeemed entries are removed by
        // the grant handlers themselves.
        var deviceCodes = await DeleteAsync(db, db.DeviceCodes, d => d.ExpiresAt < now, cancellationToken);

        // Logout redirect references: single use, five minute lifetime.
        var redirects = await DeleteAsync(db, db.LogoutRedirectReferences, r => r.ExpiresAt < now, cancellationToken);

        // Email confirmations: an expired token can never be redeemed.
        var confirmations = await DeleteAsync(db, db.EmailConfirmations, e => e.ExpiresAt < now, cancellationToken);

        // Tenant invitations: only the ones that expired while still pending. Accepted and revoked
        // invitations are kept as history.
        var invitations = await DeleteAsync(
            db,
            db.TenantInvitations,
            i => i.Status == TenantInvitationStatus.Pending && i.ExpiresAt < now,
            cancellationToken);

        // OP sessions (F1): a session that ended a day ago can no longer be referenced by a cookie.
        var sessionCutoff = now - OpSessionRetention;
        var sessions = await DeleteAsync(db, db.OpSessions, s => s.ExpiresAt < sessionCutoff, cancellationToken);
        var sessionClients = await DeleteAsync(db, db.OpSessionClients, c => c.FirstSeenAt < sessionCutoff, cancellationToken);

        return codes + deviceCodes + redirects + confirmations + invitations + sessions + sessionClients;
    }

    /// <summary>
    /// Deletes the matching rows. Uses a set-based DELETE on a relational store and falls back to tracked
    /// deletes on providers without support for it (the in-memory provider used by unit tests).
    /// </summary>
    private static async Task<int> DeleteAsync<TEntity>(
        AuthDbContext db,
        DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (db.Database.IsRelational())
        {
            return await set.IgnoreQueryFilters().Where(predicate).ExecuteDeleteAsync(cancellationToken);
        }

        var rows = await set.IgnoreQueryFilters().Where(predicate).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return 0;
        }

        set.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
