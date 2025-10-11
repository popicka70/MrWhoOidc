using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

/// <summary>
/// List and manage OAuth/OIDC scopes.
/// NOTE: Scopes are GLOBAL resources shared across all tenants (no TenantId).
/// Tenant admins can VIEW scopes, but only platform admins can DELETE them.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : TenantAwarePageModel(tenantAccessor)
{
    public IReadOnlyList<Scope> Scopes { get; private set; } = Array.Empty<Scope>();
    public bool IsPlatformAdmin { get; private set; }

    public async Task OnGetAsync()
    {
        // Check if user is platform admin
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;

        // Scopes are global/shared across tenants (no TenantId in schema)
        Scopes = await db.Scopes.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        // Only platform admins can delete global scopes
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        if (!platformAdminResult.Succeeded)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(name)) return TenantAwareRedirectToPage();
        var inUse = await db.ClientScopes.AnyAsync(cs => cs.ScopeName == name);
        if (inUse)
        {
            TempData["Error"] = $"Cannot delete scope '{name}' because it is assigned to one or more clients.";
            return TenantAwareRedirectToPage();
        }
        var entity = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
        if (entity is null) return TenantAwareRedirectToPage();
        db.Scopes.Remove(entity);
        await db.SaveChangesAsync();
        return TenantAwareRedirectToPage();
    }
}
