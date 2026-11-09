using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class IndexModel(
    AuthDbContext db,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    public sealed record TenantRow(
        Guid Id,
        string Slug,
        string Name,
        string? Description,
        string IssuerUri,
        TenantStatus Status,
        int UserCount,
        int ClientCount,
        int MaxUsers,
        int MaxClients,
        DateTimeOffset CreatedAt,
        bool IsDefaultTenant);

    public IReadOnlyList<TenantRow> TenantRows { get; private set; } = Array.Empty<TenantRow>();

    public string CurrentMode => multiTenancyOptions.Enabled ? "MultiTenant" : "SingleTenant";

    public string? DefaultTenantSlug => multiTenancyOptions.DefaultTenantSlug;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public async Task OnGetAsync()
    {
        // Redirect to dashboard if multi-tenancy is disabled
        if (!multiTenancyOptions.Enabled)
        {
            Response.Redirect("/PlatformAdmin/Index");
            return;
        }

        var defaultSlug = multiTenancyOptions.DefaultTenantSlug;
        var search = Search?.Trim();

        // Soft-deleted tenants are excluded: they stay in the database for retention but no longer
        // appear in the operational list.
        var query = db.Tenants
            .AsNoTracking()
            .Where(t => t.Status != TenantStatus.Deleted);

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(t => t.Name.Contains(search) || t.Slug.Contains(search));
        }

        // Load tenants with counts
        var tenants = await query
            .OrderBy(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id,
                t.Slug,
                t.Name,
                t.Description,
                t.IssuerUri,
                t.Status,
                t.MaxUsers,
                t.MaxClients,
                t.CreatedAt,
                UserCount = db.Users.Count(u => u.TenantId == t.Id),
                ClientCount = db.Clients.Count(c => c.TenantId == t.Id),
                IsDefaultTenant = t.Slug == defaultSlug
            })
            .ToListAsync();

        TenantRows = tenants.Select(t => new TenantRow(
            t.Id,
            t.Slug,
            t.Name,
            t.Description,
            t.IssuerUri,
            t.Status,
            t.UserCount,
            t.ClientCount,
            t.MaxUsers,
            t.MaxClients,
            t.CreatedAt,
            t.IsDefaultTenant
        )).ToList();
    }

    /// <summary>
    /// Suspends (disables) or re-activates a tenant. Tenant resolution only serves Active tenants,
    /// so suspending immediately takes the tenant's URLs offline. The default tenant is refused:
    /// the platform administration surface (including this page) lives there.
    /// </summary>
    public async Task<IActionResult> OnPostToggleStatusAsync(Guid id)
    {
        var defaultSlug = multiTenancyOptions.DefaultTenantSlug;

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is null)
        {
            TempData["Error"] = "Tenant not found.";
            return RedirectToPage();
        }

        if (!string.IsNullOrEmpty(defaultSlug) && tenant.Slug == defaultSlug)
        {
            TempData["Error"] = $"The default tenant '{tenant.Slug}' cannot be suspended — platform administration lives there.";
            return RedirectToPage();
        }

        if (tenant.Status == TenantStatus.Deleted)
        {
            TempData["Error"] = "This tenant is deleted and cannot be re-activated from this page.";
            return RedirectToPage();
        }

        if (tenant.Status == TenantStatus.Active)
        {
            tenant.Status = TenantStatus.Suspended;
            tenant.SuspendedAt = DateTimeOffset.UtcNow;
            TempData["Success"] = $"Tenant '{tenant.Name}' suspended. Its URLs no longer resolve until re-enabled.";
        }
        else
        {
            tenant.Status = TenantStatus.Active;
            tenant.SuspendedAt = null;
            TempData["Success"] = $"Tenant '{tenant.Name}' re-enabled.";
        }

        await db.SaveChangesAsync();
        return RedirectToPage();
    }
}
