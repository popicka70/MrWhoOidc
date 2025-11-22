using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin;

/// <summary>
/// Dedicated impersonation management page for platform admins.
/// Provides a centralized UI for starting/stopping tenant impersonation.
/// </summary>
[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class ImpersonationModel(
    AuthDbContext db,
    IImpersonationService impersonationService,
    IOptions<MultiTenancyOptions> multiTenancyOptions) : PageModel
{
    public List<TenantDto> Tenants { get; set; } = new();
    public ImpersonationInfo? CurrentImpersonation { get; set; }

    public async Task OnGetAsync()
    {
        // Get current impersonation status
        CurrentImpersonation = await impersonationService.GetImpersonationInfoAsync(HttpContext);

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

    public async Task<IActionResult> OnPostStartImpersonationAsync(Guid tenantId)
    {
        var success = await impersonationService.StartImpersonationAsync(HttpContext, User, tenantId);

        if (!success)
        {
            TempData["Error"] = "Failed to start impersonation. The tenant may be inactive or you don't have permission.";
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

        TempData["Success"] = $"Now impersonating tenant: {tenant.Name}. All write operations are disabled.";

        // Redirect to tenant admin dashboard
        // In multi-tenant mode: /t/{slug}/Admin/Index
        // In single-tenant mode: /Admin/Index
        if (multiTenancyOptions.Value.Enabled)
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
