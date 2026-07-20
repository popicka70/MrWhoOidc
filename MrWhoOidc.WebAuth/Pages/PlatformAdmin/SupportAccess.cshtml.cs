using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin;

/// <summary>
/// Dedicated support access management page for platform admins.
/// Provides a centralized UI for starting/stopping tenant support access.
/// </summary>
[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class SupportAccessModel(
    AuthDbContext db,
    ITenantSupportAccessService supportAccessService,
    IMultiTenancyOptions multiTenancyOptions,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions) : PageModel
{
    public List<TenantDto> Tenants { get; set; } = new();
    public TenantSupportAccessInfo? CurrentSupportAccess { get; set; }

    public async Task OnGetAsync()
    {
        // Check feature flag: Tenant Support Access must be enabled
        var options = authOptions.Value;
        if (!options.EnableTenantSupportAccess)
        {
            // Return a 404 or 'Feature Disabled' message
            TempData["Error"] = "Feature Disabled: Tenant Support Access is not enabled.";
            return;
        }

        // Get current support access status
        CurrentSupportAccess = await supportAccessService.GetSupportAccessInfoAsync(HttpContext);

        // Load all tenants with their counts
        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderByDescending(t => t.Status == TenantStatus.Active)
            .ThenBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.Status,
                t.CreatedAt,
                UserCount = db.Users.Count(u => u.TenantId == t.Id),
                ClientCount = db.Clients.Count(c => c.TenantId == t.Id)
            })
            .ToListAsync();

        Tenants = tenants.Select(t => new TenantDto
        {
            Id = t.Id,
            Name = t.Name,
            Slug = t.Slug,
            IsActive = t.Status == TenantStatus.Active,
            CreatedAt = t.CreatedAt.DateTime,
            UserCount = t.UserCount,
            ClientCount = t.ClientCount
        }).ToList();
    }

    public async Task<IActionResult> OnPostStartSupportAccessAsync(Guid tenantId, string reason, int? expiryMinutes = null, string? ticketReference = null)
    {
        // Validate reason is provided
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Reason is required to start support access.";
            return RedirectToPage();
        }

        var success = await supportAccessService.StartSupportAccessAsync(HttpContext, User, tenantId, reason, expiryMinutes, ticketReference);

        if (!success)
        {
            TempData["Error"] = "Failed to start support access. The tenant may be inactive or you don't have permission.";
            return RedirectToPage();
        }

        // Get tenant info for success message and redirect
        var tenant = await db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Name, t.Slug })
            .FirstOrDefaultAsync();

        if (tenant == null)
        {
            TempData["Error"] = "Tenant not found.";
            return RedirectToPage();
        }

        TempData["Success"] = $"Support access active for tenant: {tenant.Name}. All write operations are disabled.";

        // Redirect to tenant admin dashboard
        // In multi-tenant mode: /t/{slug}/Admin/Index
        // In single-tenant mode: /Admin/Index
        if (multiTenancyOptions.Enabled)
        {
            return Redirect($"/t/{tenant.Slug}/Admin/Index");
        }
        return RedirectToPage("/Admin/Index");
    }

    public class TenantDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public int UserCount { get; set; }
        public int ClientCount { get; set; }
    }
}
