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

    // No DTOs needed; reading state directly
}
