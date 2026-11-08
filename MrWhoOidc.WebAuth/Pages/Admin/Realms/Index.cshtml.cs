using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Pages.Admin;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record RealmRow(
        Guid Id,
        string Name,
        string? DisplayName,
        DateTimeOffset CreatedAt,
        Guid TenantId,
        string TenantName,
        bool AllowUnconfirmedLogin,
        bool IsSystem);

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
                x.Realm.AllowUnconfirmedLogin,
                x.Realm.Name == SystemRealms.Admin
                    || x.Realm.Name == SystemRealms.Default
                    || x.Realm.Name == SystemRealms.Platform
            ))
            .ToListAsync();
    }
}
