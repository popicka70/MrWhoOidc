using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : PageModel
{
    public sealed record RealmRow(Guid Id, string Name, string? DisplayName, DateTimeOffset CreatedAt, Guid TenantId, string TenantName);

    public IReadOnlyList<RealmRow> Realms { get; private set; } = Array.Empty<RealmRow>();
    public List<SelectListItem> TenantOptions { get; private set; } = new();
    public bool IsPlatformAdmin { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    public async Task OnGetAsync()
    {
        // Check if user is platform admin
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;

        // Load tenant options for filter (platform admins only)
        if (IsPlatformAdmin)
        {
            var tenants = await db.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active)
                .OrderBy(t => t.Name)
                .ToListAsync();
            TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
            TenantOptions.Insert(0, new SelectListItem("All Tenants", ""));
        }

        var q = db.Realms.AsNoTracking()
            .Join(db.Tenants, r => r.TenantId, t => t.Id, (r, t) => new { Realm = r, Tenant = t });

        // Automatic tenant scoping
        if (IsPlatformAdmin)
        {
            // Platform admins can optionally filter by tenant
            if (TenantId.HasValue)
            {
                q = q.Where(x => x.Realm.TenantId == TenantId.Value);
            }
        }
        else
        {
            // Regular tenant admins only see their tenant
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (currentTenantId.HasValue)
            {
                q = q.Where(x => x.Realm.TenantId == currentTenantId.Value);
            }
            else
            {
                // No tenant context, return empty
                Realms = Array.Empty<RealmRow>();
                return;
            }
        }

        Realms = await q
            .OrderBy(x => x.Realm.Name)
            .Select(x => new RealmRow(
                x.Realm.Id,
                x.Realm.Name,
                x.Realm.DisplayName,
                x.Realm.CreatedAt,
                x.Realm.TenantId,
                x.Tenant.Name
            ))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == id);
        if (realm is null)
        {
            // Build tenant-aware redirect URL
            var currentTenant = tenantAccessor.CurrentTenant;
            var redirectUrl = currentTenant != null 
                ? $"/t/{currentTenant.Slug}/Admin/Realms" + (TenantId.HasValue ? $"?TenantId={TenantId}" : "")
                : "/Admin/Realms" + (TenantId.HasValue ? $"?TenantId={TenantId}" : "");
            return Redirect(redirectUrl);
        }
        db.Realms.Remove(realm);
        await db.SaveChangesAsync();
        
        // Build tenant-aware redirect URL
        var tenant = tenantAccessor.CurrentTenant;
        var url = tenant != null 
            ? $"/t/{tenant.Slug}/Admin/Realms" + (TenantId.HasValue ? $"?TenantId={TenantId}" : "")
            : "/Admin/Realms" + (TenantId.HasValue ? $"?TenantId={TenantId}" : "");
        return Redirect(url);
    }
}
