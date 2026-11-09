using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants;

/// <summary>
/// Confirmation page for soft-deleting a tenant.
/// Soft delete keeps all tenant data in the database (retention) but sets the tenant's status to
/// Deleted, which immediately stops tenant resolution — its URLs no longer answer. The default
/// tenant is never deletable: platform administration lives there.
/// </summary>
[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class DeleteModel(
    AuthDbContext db,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    public Guid TenantId { get; private set; }
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public TenantStatus Status { get; private set; }
    public int UserCount { get; private set; }
    public int ClientCount { get; private set; }
    public bool Found { get; private set; }
    public bool IsDefaultTenant { get; private set; }

    private async Task LoadAsync(Guid id)
    {
        var row = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.Status,
                UserCount = db.Users.Count(u => u.TenantId == t.Id),
                ClientCount = db.Clients.Count(c => c.TenantId == t.Id),
                IsDefaultTenant = t.Slug == multiTenancyOptions.DefaultTenantSlug
            })
            .FirstOrDefaultAsync();

        if (row is null)
        {
            return;
        }

        TenantId = row.Id;
        TenantName = row.Name;
        TenantSlug = row.Slug;
        Status = row.Status;
        UserCount = row.UserCount;
        ClientCount = row.ClientCount;
        IsDefaultTenant = row.IsDefaultTenant;
        Found = true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        await LoadAsync(id);
        if (!Found)
        {
            return NotFound();
        }

        if (IsDefaultTenant)
        {
            TempData["Error"] = $"The default tenant '{TenantSlug}' cannot be deleted — platform administration lives there.";
            return RedirectToPage("Index");
        }

        if (Status == TenantStatus.Deleted)
        {
            TempData["Error"] = "This tenant is already deleted.";
            return RedirectToPage("Index");
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is not null)
        {
            tenant.Status = TenantStatus.Deleted;
            tenant.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            TempData["Success"] = $"Tenant '{tenant.Name}' deleted. Its data is retained in the database; its URLs no longer resolve.";
        }

        return RedirectToPage("Index");
    }
}
