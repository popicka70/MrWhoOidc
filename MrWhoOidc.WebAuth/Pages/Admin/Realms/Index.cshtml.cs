using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record RealmRow(Guid Id, string Name, string? DisplayName, DateTimeOffset CreatedAt, Guid TenantId, string TenantName, bool AllowUnconfirmedLogin);

    public IReadOnlyList<RealmRow> Realms { get; private set; } = Array.Empty<RealmRow>();

    public async Task OnGetAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Realms = Array.Empty<RealmRow>();
            return;
        }

        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == currentTenantId.Value)
            .Join(db.Tenants, r => r.TenantId, t => t.Id, (r, t) => new { Realm = r, Tenant = t })
            .OrderBy(x => x.Realm.Name)
            .Select(x => new RealmRow(
                x.Realm.Id,
                x.Realm.Name,
                x.Realm.DisplayName,
                x.Realm.CreatedAt,
                x.Realm.TenantId,
                x.Tenant.Name,
                x.Realm.AllowUnconfirmedLogin
            ))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Realms");
        }

        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value);
        if (realm is null)
        {
            return TenantAwareRedirect("/Admin/Realms");
        }
        db.Realms.Remove(realm);
        await db.SaveChangesAsync();

        return TenantAwareRedirect("/Admin/Realms");
    }
}
