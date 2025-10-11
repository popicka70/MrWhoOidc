using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

/// <summary>
/// List and manage OAuth/OIDC scopes (both global and tenant-scoped).
/// Platform admins can see ALL scopes. Tenant admins see global + their tenant's scopes.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : TenantAwarePageModel(tenantAccessor)
{
    public sealed record ScopeRow(string Name, string? Description, bool IsExposed, bool IsGlobal, Guid? TenantId, string? TenantName);
    
    public IReadOnlyList<ScopeRow> Scopes { get; private set; } = Array.Empty<ScopeRow>();
    public bool IsPlatformAdmin { get; private set; }

    public async Task OnGetAsync()
    {
        // Check if user is platform admin
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;

        // Platform admins see ALL scopes, tenant admins see global + their tenant's scopes
        var query = db.Scopes.AsNoTracking();
        
        if (!IsPlatformAdmin)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (currentTenantId.HasValue)
            {
                query = query.Where(s => s.IsGlobal || s.TenantId == currentTenantId.Value);
            }
            else
            {
                // No tenant context, show only global
                query = query.Where(s => s.IsGlobal);
            }
        }

        Scopes = await query
            .GroupJoin(
                db.Tenants.AsNoTracking(),
                s => s.TenantId,
                t => (Guid?)t.Id,
                (s, tenants) => new { Scope = s, Tenant = tenants.FirstOrDefault() })
            .Select(x => new ScopeRow(
                x.Scope.Name,
                x.Scope.Description,
                x.Scope.IsExposed,
                x.Scope.IsGlobal,
                x.Scope.TenantId,
                x.Tenant != null ? x.Tenant.Name : null))
            .OrderBy(s => s.IsGlobal ? 0 : 1) // Global first
            .ThenBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return TenantAwareRedirectToPage();
        
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Name == name);
        if (scope is null) return TenantAwareRedirectToPage();

        // Platform admins can delete any scope
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        if (!platformAdminResult.Succeeded)
        {
            // Tenant admins can only delete their own tenant-scoped scopes
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (scope.IsGlobal || !currentTenantId.HasValue || scope.TenantId != currentTenantId.Value)
            {
                return Forbid();
            }
        }

        // Check if scope is in use
        var inUse = await db.ClientScopes.AnyAsync(cs => cs.ScopeName == name);
        if (inUse)
        {
            TempData["Error"] = $"Cannot delete scope '{name}' because it is assigned to one or more clients.";
            return TenantAwareRedirectToPage();
        }
        
        db.Scopes.Remove(scope);
        await db.SaveChangesAsync();
        TempData["Success"] = $"Scope '{name}' deleted successfully.";
        return TenantAwareRedirectToPage();
    }
}

