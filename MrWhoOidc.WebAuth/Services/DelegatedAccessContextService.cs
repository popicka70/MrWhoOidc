using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.WebAuth.Observability;

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
    IDelegableCapabilityCatalog capabilityCatalog,
    IUserAccountService userAccountService,
    ILogger<DelegatedAccessContextService> logger)
    : IDelegatedAccessContextService
{
    private const string ActiveGrantIdKey = "ActiveDelegatedGrantId";

    public async Task<DelegatedAccessContextInfo?> GetActiveContextAsync(HttpContext context)
    {
        // Read active grant ID from session
        var grantIdStr = context.Items[ActiveGrantIdKey] as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(grantIdStr))
        {
            return null;
        }

        var grantId = Guid.Parse(grantIdStr);
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

        var delegatorUser = await userAccountService.FindByAccountIdAsync(grant.DelegatorUserAccountId);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == grant.TenantId);

        var capabilities = ParseCapabilities(grant.CapabilitiesJson);
        var remaining = grant.ExpiresAt - DateTimeOffset.UtcNow;

        return new DelegatedAccessContextInfo(
            DelegatorName: delegatorUser?.Name ?? delegatorUser?.Username ?? "Unknown",
            TenantName: tenant?.Name ?? "Unknown Tenant",
            ActiveCapabilities: capabilities,
            RemainingTime: remaining.Humanize(),
            ExpiresAt: grant.ExpiresAt);
    }

    public async Task SetActiveGrantAsync(HttpContext context, Guid grantId)
    {
        context.Items[ActiveGrantIdKey] = grantId.ToString();
        logger.LogInformation("Delegated access grant {GrantId} activated in session context.", grantId);
    }

    public async Task ClearActiveGrantAsync(HttpContext context)
    {
        context.Items[ActiveGrantIdKey] = string.Empty;
        logger.LogInformation("Delegated access context cleared from session.");
    }

    private static List<string> ParseCapabilities(string json)
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
        return parsed ?? new List<string>();
    }
}
