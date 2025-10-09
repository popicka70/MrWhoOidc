using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : PageModel
{
    public IReadOnlyList<IdentityProvider> Providers { get; private set; } = Array.Empty<IdentityProvider>();
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

        // Build query with tenant JOIN
        var q = db.IdentityProviders.AsNoTracking()
            .Join(db.Tenants, p => p.TenantId, t => t.Id, (p, t) => new { Provider = p, Tenant = t });

        // Automatic tenant scoping
        if (IsPlatformAdmin)
        {
            // Platform admins can optionally filter by tenant
            if (TenantId.HasValue)
            {
                q = q.Where(x => x.Provider.TenantId == TenantId.Value);
            }
        }
        else
        {
            // Regular tenant admins only see their tenant
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (currentTenantId.HasValue)
            {
                q = q.Where(x => x.Provider.TenantId == currentTenantId.Value);
            }
            else
            {
                // No tenant context, return empty
                Providers = Array.Empty<IdentityProvider>();
                return;
            }
        }

        Providers = await q
            .OrderBy(x => x.Provider.SortOrder)
            .ThenBy(x => x.Provider.Name)
            .Select(x => x.Provider)
            .ToListAsync();
    }

    public sealed record ReorderInput(Guid[] ProviderIds);

    public async Task<IActionResult> OnPostReorderAsync([FromBody] ReorderInput input)
    {
        if (input?.ProviderIds is null || input.ProviderIds.Length == 0)
            return BadRequest(new { error = "empty_order" });

        // Load existing providers and validate ids set equality
        var providers = await db.IdentityProviders.ToListAsync();
        var existingIds = providers.Select(p => p.Id).ToHashSet();
        if (!input.ProviderIds.All(id => existingIds.Contains(id)) || existingIds.Count != input.ProviderIds.Length)
            return BadRequest(new { error = "mismatch", message = "Posted provider list does not match existing set." });

        // Assign sequential SortOrder starting at 1 (leave gap of 10 for manual future insertions if desired?)
        var order = 1;
        foreach (var id in input.ProviderIds)
        {
            var p = providers.First(x => x.Id == id);
            if (p.SortOrder != order)
            {
                p.SortOrder = order;
            }
            order += 1;
        }

        await db.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }
}
