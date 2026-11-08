using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using System.Net.Http.Json;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Backchannel;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    MrWhoOidc.WebAuth.Background.BackchannelRuntimeState state,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record Item(Guid Id, string ClientId, string TargetUri, string Status, int AttemptCount, int MaxAttempts, int? LastHttpStatus, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt);
    public sealed record CircuitItem(string ClientId, int Failures, DateTimeOffset? OpenUntil);

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    public long Backlog { get; private set; }
    public bool Enabled { get; private set; }
    public List<Item> Items { get; private set; } = new();
    public List<CircuitItem> OpenCircuits { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Items = new List<Item>();
            OpenCircuits = new List<CircuitItem>();
            return;
        }

        // Query API for items and backlog scoped to tenant
        var q = db.BackchannelLogoutNotifications.AsNoTracking()
            .Where(n => n.TenantId == currentTenantId.Value);

        if (!string.IsNullOrWhiteSpace(Status)) q = q.Where(n => n.Status == Status);
        Items = await q.OrderByDescending(n => n.CreatedAt)
            .Take(200)
            .Select(n => new Item(n.Id, n.ClientId, n.TargetUri, n.Status, n.AttemptCount, n.MaxAttempts, n.LastHttpStatus, n.LastError, n.CreatedAt, n.LastAttemptAt, n.NextAttemptAt))
            .ToListAsync();
        var now = DateTimeOffset.UtcNow;
        Backlog = await db.BackchannelLogoutNotifications.LongCountAsync(n => n.TenantId == currentTenantId.Value && n.Status == "pending" && (n.NextAttemptAt == null || n.NextAttemptAt <= now));

        // Read runtime state for circuits and flag
        Enabled = state.EmissionEnabled;
        var tenantClientIds = await db.Clients.AsNoTracking()
            .Where(c => c.TenantId == currentTenantId.Value)
            .Select(c => c.ClientId)
            .ToListAsync();
        var clientSet = tenantClientIds.ToHashSet(StringComparer.Ordinal);
        OpenCircuits = state.Circuits
            .Where(kv => kv.Value.OpenUntil is not null && kv.Value.OpenUntil > DateTimeOffset.UtcNow)
            .Where(kv => clientSet.Contains(kv.Key))
            .Select(kv => new CircuitItem(kv.Key, kv.Value.Failures, kv.Value.OpenUntil))
            .OrderByDescending(c => c.Failures)
            .Take(50)
            .ToList();
    }

    public async Task<IActionResult> OnPostRetryAsync([FromForm] Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Backchannel");
        }

        var entity = await db.BackchannelLogoutNotifications.FirstOrDefaultAsync(n => n.Id == id && n.TenantId == currentTenantId.Value);
        if (entity is not null)
        {
            entity.Status = "pending";
            entity.NextAttemptAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        return TenantAwareRedirect("/Admin/Backchannel", new { status = Status });
    }

    /// <summary>
    /// Bulk action: requeue every failed or dead-lettered backchannel logout for the current tenant.
    /// </summary>
    public async Task<IActionResult> OnPostRetryFailedAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Backchannel");
        }

        var now = DateTimeOffset.UtcNow;
        var failed = await db.BackchannelLogoutNotifications
            .Where(n => n.TenantId == currentTenantId.Value && (n.Status == "failed" || n.Status == "dead_letter"))
            .ToListAsync();

        foreach (var entity in failed)
        {
            entity.Status = "pending";
            entity.NextAttemptAt = now;
        }

        await db.SaveChangesAsync();
        TempData["Success"] = failed.Count > 0
            ? $"Requeued {failed.Count} failed backchannel logout(s) for immediate retry."
            : "No failed or dead-lettered backchannel logouts to retry.";
        return TenantAwareRedirect("/Admin/Backchannel", new { status = Status });
    }

    /// <summary>
    /// Bulk action: remove delivered backchannel logout notifications (status succeeded) for the
    /// current tenant. Failed and pending entries are kept.
    /// </summary>
    public async Task<IActionResult> OnPostClearSentAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Backchannel");
        }

        var sent = await db.BackchannelLogoutNotifications
            .Where(n => n.TenantId == currentTenantId.Value && n.Status == "succeeded")
            .ToListAsync();

        if (sent.Count > 0)
        {
            db.BackchannelLogoutNotifications.RemoveRange(sent);
            await db.SaveChangesAsync();
        }

        TempData["Success"] = sent.Count > 0
            ? $"Cleared {sent.Count} delivered backchannel logout(s)."
            : "No delivered backchannel logouts to clear.";
        return TenantAwareRedirect("/Admin/Backchannel", new { status = Status });
    }

    // No DTOs needed; reading state directly
}
