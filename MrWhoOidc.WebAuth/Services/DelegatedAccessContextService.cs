using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.WebAuth.Observability;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Session-based service for managing active delegated access context.
/// Stores/retrieves the active grant reference in ASP.NET session and
/// resolves display information (delegator name, tenant, capabilities, expiry).
/// Used by the dual-identity banner and activation/exit pages.
/// </summary>
public interface IDelegatedAccessContextService
{
    /// <summary>
    /// Get the currently active delegated access context info for display.
    /// Returns null if no delegated context is active.
    /// </summary>
    Task<DelegatedAccessContextInfo?> GetActiveContextAsync(HttpContext context);

    /// <summary>
    /// Store an active grant ID in the session context.
    /// </summary>
    Task SetActiveGrantAsync(HttpContext context, Guid grantId);

    /// <summary>
    /// Clear the active grant reference from session.
    /// </summary>
    Task ClearActiveGrantAsync(HttpContext context);
}

/// <summary>
/// Display information for an active delegated access context.
/// </summary>
public sealed record DelegatedAccessContextInfo(
    string DelegatorName,
    string TenantName,
    List<string> ActiveCapabilities,
    string RemainingTime,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Implementation using the AuthDbContext for grant lookups and ASP.NET session for context.
/// </summary>
internal sealed class DelegatedAccessContextService(
    AuthDbContext db,
    IOptions<AuthOptions> authOptions,
    ILogger<DelegatedAccessContextService> logger)
    : IDelegatedAccessContextService
{
    private const string ActiveGrantIdKey = "DelegatedAccessGrantId";

    public async Task<DelegatedAccessContextInfo?> GetActiveContextAsync(HttpContext context)
    {
        // Read active grant ID from session
        var grantIdStr = context.Session.GetString(ActiveGrantIdKey);
        if (string.IsNullOrWhiteSpace(grantIdStr))
        {
            return null;
        }

        if (!Guid.TryParse(grantIdStr, out var grantId))
        {
            await ClearActiveGrantAsync(context)
                .ConfigureAwait(false);
            return null;
        }

        var grant = await db.DelegatedAccessGrants
            .AsNoTracking()
            .Where(g => g.Id == grantId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (grant is null)
        {
            // Grant no longer exists — clear stale session reference
            await ClearActiveGrantAsync(context)
                .ConfigureAwait(false);
            return null;
        }

        if (grant.Status != DelegatedAccessGrantStatus.Active)
        {
            // Grant is no longer active — clear stale session reference
            await ClearActiveGrantAsync(context)
                .ConfigureAwait(false);
            return null;
        }

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            // Grant has expired — clear stale session reference
            await ClearActiveGrantAsync(context)
                .ConfigureAwait(false);
            return null;
        }

        var delegatorUser = await db.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == grant.DelegatorUserAccountId);

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == grant.TenantId);

        var capabilities = ParseCapabilities(grant.CapabilitiesJson);
        var remaining = grant.ExpiresAt - DateTimeOffset.UtcNow;

        string remainingTime;
        if (remaining.TotalMinutes < 1)
        {
            remainingTime = "< 1 min";
        }
        else if (remaining.TotalHours < 1)
        {
            remainingTime = $"{(int)remaining.TotalMinutes} min";
        }
        else
        {
            remainingTime = $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return new DelegatedAccessContextInfo(
            DelegatorName: delegatorUser?.Name ?? delegatorUser?.Username ?? "Unknown",
            TenantName: tenant?.Name ?? "Unknown Tenant",
            ActiveCapabilities: capabilities,
            RemainingTime: remainingTime,
            ExpiresAt: grant.ExpiresAt);
    }

    public async Task SetActiveGrantAsync(HttpContext context, Guid grantId)
    {
        if (!authOptions.Value.EnableDelegatedAccess)
        {
            throw new AuthorizationError("Delegated access is disabled.");
        }

        var actorClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(actorClaim, out var actorId))
        {
            throw new AuthorizationError("Authenticated user account ID is not available.");
        }

        var now = DateTimeOffset.UtcNow;
        var grant = await db.DelegatedAccessGrants.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == grantId)
            .ConfigureAwait(false);
        if (grant is null || grant.DelegateUserAccountId != actorId)
        {
            throw new NotFoundError("Delegated access grant not found.");
        }
        if (grant.Status != DelegatedAccessGrantStatus.Active
            || grant.StartsAt is not null && grant.StartsAt > now
            || grant.ExpiresAt <= now)
        {
            throw new StatusError("Delegated access grant is not active.");
        }

        var activeMembershipCount = await db.UserTenantMemberships.AsNoTracking()
            .CountAsync(membership => membership.TenantId == grant.TenantId
                && (membership.UserAccountId == grant.DelegatorUserAccountId
                    || membership.UserAccountId == grant.DelegateUserAccountId)
                && membership.Status == TenantMembershipStatus.Active
                && (membership.ExpiresAt == null || membership.ExpiresAt > now))
            .ConfigureAwait(false);
        if (activeMembershipCount != 2)
        {
            throw new MembershipError("Both parties must have active tenant memberships.");
        }

        context.Session.SetString(ActiveGrantIdKey, grantId.ToString());
        logger.LogInformation("Delegated access grant {GrantId} activated in session context.", grantId);
    }

    public async Task ClearActiveGrantAsync(HttpContext context)
    {
        context.Session.Remove(ActiveGrantIdKey);
        logger.LogInformation("Delegated access context cleared from session.");
    }

    private static List<string> ParseCapabilities(string json)
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
        return parsed ?? new List<string>();
    }
}
