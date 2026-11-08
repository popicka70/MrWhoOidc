using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

/// <summary>
/// Confirmation page for deleting a tenant realm.
/// Seeded system realms (admin, default, platform) are never deletable: they hold the admin user, the
/// default client namespace and the platform-admin role the tenant depends on. The guard runs on POST,
/// not only in the UI, so a crafted request is refused as well.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class DeleteModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public string? RealmName { get; private set; }
    public bool Found { get; private set; }
    public bool IsSystemRealm { get; private set; }
    public int ClientCount { get; private set; }

    private async Task LoadAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return;
        }

        var realm = await db.Realms.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value);
        if (realm is null)
        {
            return;
        }

        RealmName = realm.Name;
        IsSystemRealm = SystemRealms.IsProtected(realm.Name);
        ClientCount = await db.Clients.AsNoTracking().CountAsync(c => c.RealmId == id);
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

        if (IsSystemRealm)
        {
            TempData["Error"] = $"The realm '{RealmName}' is a system realm and cannot be deleted.";
            return TenantAwareRedirect("/Admin/Realms");
        }

        if (ClientCount > 0)
        {
            TempData["Error"] = $"The realm '{RealmName}' still has {ClientCount} client(s). Delete them first.";
            return TenantAwareRedirect("/Admin/Realms");
        }

        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        var realm = currentTenantId.HasValue
            ? await db.Realms.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value)
            : null;
        if (realm is not null)
        {
            db.Realms.Remove(realm);
            await db.SaveChangesAsync();
        }

        return TenantAwareRedirect("/Admin/Realms");
    }
}
