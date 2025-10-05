using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : PageModel
{
    public sealed record UserRow(Guid Id, string Username, string? Email, string? Name, DateTimeOffset CreatedAt, Guid TenantId, string TenantName);

    public IReadOnlyList<UserRow> Users { get; private set; } = Array.Empty<UserRow>();
    public List<SelectListItem> TenantOptions { get; private set; } = new();
    public bool IsPlatformAdmin { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

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
        var q = db.Users.AsNoTracking()
            .Join(db.Tenants, u => u.TenantId, t => t.Id, (u, t) => new { User = u, Tenant = t });

        // Automatic tenant scoping
        if (IsPlatformAdmin)
        {
            // Platform admins can optionally filter by tenant
            if (TenantId.HasValue)
            {
                q = q.Where(x => x.User.TenantId == TenantId.Value);
            }
        }
        else
        {
            // Regular tenant admins only see their tenant
            var currentTenantId = tenantAccessor.CurrentTenant?.TenantId;
            if (currentTenantId.HasValue)
            {
                q = q.Where(x => x.User.TenantId == currentTenantId.Value);
            }
            else
            {
                // No tenant context, return empty
                Users = Array.Empty<UserRow>();
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(x => x.User.Username.Contains(s) || (x.User.Email != null && x.User.Email.Contains(s)) || (x.User.Name != null && x.User.Name.Contains(s)));
        }

        Users = await q
            .OrderBy(x => x.User.Username)
            .Select(x => new UserRow(
                x.User.Id,
                x.User.Username,
                x.User.Email,
                x.User.Name,
                x.User.CreatedAt,
                x.User.TenantId,
                x.Tenant.Name
            ))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var inUse = await db.Tokens.AnyAsync(t => t.UserId == id)
            || await db.Consents.AnyAsync(c => c.UserId == id)
            || await db.UserClientAssignments.AnyAsync(a => a.UserId == id)
            || await db.UserRoleAssignments.AnyAsync(a => a.UserId == id);
        if (inUse)
        {
            TempData["Error"] = "Cannot delete user; it is referenced by tokens, consents, or assignments.";
            return RedirectToPage();
        }
        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (entity is null) return RedirectToPage();
        db.Users.Remove(entity);
        await db.SaveChangesAsync();
        return RedirectToPage(new { TenantId });
    }
}
