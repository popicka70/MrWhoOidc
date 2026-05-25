using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages.Admin;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Registrations;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    IRegistrationWorkflowService registrationService) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public IReadOnlyList<ItemVm> Items { get; private set; } = Array.Empty<ItemVm>();

    public record ItemVm(Guid Id, string Email, string? FirstName, string? LastName, string? ClientDisplay, string State, string CreatedAtLocal, string? Decision);

    public async Task OnGetAsync()
    {
        var currentTenant = TenantAccessor.CurrentTenant;
        var currentTenantId = currentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Items = Array.Empty<ItemVm>();
            return;
        }

        // Build query scoped to current tenant
        var isDefaultTenant = IsDefaultTenant(currentTenant?.Slug);
        var q = db.Set<Registration>().AsNoTracking()
            .Where(r => r.TenantId == currentTenantId.Value && !r.IsPlatformRegistration);

        if (isDefaultTenant)
        {
            q = q.Where(r => r.IsTenantAdmin || r.ClientId != null);
        }

        var regs = await q.OrderByDescending(r => r.CreatedAt).ToListAsync();

        var clientIds = regs.Where(r => r.ClientId.HasValue).Select(r => r.ClientId!.Value).Distinct().ToArray();
        var clientMap = await db.Clients.AsNoTracking()
            .Where(c => clientIds.Contains(c.Id) && c.TenantId == currentTenantId.Value)
            .Join(db.Realms.AsNoTracking(), c => c.RealmId, rl => rl.Id, (c, rl) => new { c.Id, Display = $"{c.ClientId} ({rl.Name})" })
            .ToDictionaryAsync(x => x.Id, x => x.Display);

        Items = regs.Select(r =>
        {
            clientMap.TryGetValue(r.ClientId ?? Guid.Empty, out var display);
            var created = r.CreatedAt.ToLocalTime().ToString("g");
            string? decision = r.State == "approved"
                ? (r.ApprovedAt.HasValue ? $"Approved {r.ApprovedAt.Value.ToLocalTime():g}" : "Approved")
                : r.State == "rejected"
                    ? (r.RejectedAt.HasValue ? $"Rejected {r.RejectedAt.Value.ToLocalTime():g}" : "Rejected")
                    : null;
            return new ItemVm(r.Id, r.Email, r.FirstName, r.LastName, display, r.State, created, decision);
        }).ToList();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Registrations");
        }

        var reg = await db.Set<Registration>().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value && !r.IsPlatformRegistration);
        if (reg is not null && IsDefaultTenant(TenantAccessor.CurrentTenant?.Slug) && !reg.IsTenantAdmin && reg.ClientId is null)
        {
            return TenantAwareRedirect("/Admin/Registrations");
        }
        if (reg is null) return TenantAwareRedirect("/Admin/Registrations");
        if (!string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase)) return TenantAwareRedirect("/Admin/Registrations");

        try
        {
            var userId = await registrationService.ApproveRegistrationAsync(reg, GetCurrentUserId());
            return TenantAwareRedirect($"/admin/users/edit/{userId}");
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
            return TenantAwareRedirect("/Admin/Registrations");
        }
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Registrations");
        }

        var reg = await db.Set<Registration>().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value && !r.IsPlatformRegistration);
        if (reg is not null && IsDefaultTenant(TenantAccessor.CurrentTenant?.Slug) && !reg.IsTenantAdmin && reg.ClientId is null)
        {
            return TenantAwareRedirect("/Admin/Registrations");
        }
        if (reg is null) return TenantAwareRedirect("/Admin/Registrations");
        if (!string.Equals(reg.State, "pending", StringComparison.OrdinalIgnoreCase)) return TenantAwareRedirect("/Admin/Registrations");

        reg.State = "rejected";
        reg.RejectedAt = DateTimeOffset.UtcNow;
        reg.RejectedByUserId = GetCurrentUserId();
        await db.SaveChangesAsync();
        return TenantAwareRedirect("/Admin/Registrations");
    }

    Guid? GetCurrentUserId()
    {
        var sub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private bool IsDefaultTenant(string? slug)
        => string.Equals(slug, MultiTenancyOptions.DefaultTenantSlug ?? "default", StringComparison.OrdinalIgnoreCase);
}
