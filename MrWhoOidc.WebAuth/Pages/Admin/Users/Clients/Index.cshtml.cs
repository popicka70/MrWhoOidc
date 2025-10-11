using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Clients;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : UserPageModelBase(tenantAccessor)
{
    [FromRoute]
    public Guid UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? RealmId { get; set; }

    [BindProperty]
    public Guid ClientId { get; set; }

    [BindProperty]
    public bool IsActive { get; set; } = true;

    public string TenantName { get; set; } = string.Empty;
    public Guid UserTenantId { get; set; }

    public IReadOnlyList<AssignmentVm> Assignments { get; private set; } = Array.Empty<AssignmentVm>();
    public IReadOnlyList<ClientVm> Clients { get; private set; } = Array.Empty<ClientVm>();
    public IReadOnlyList<RealmVm> Realms { get; private set; } = Array.Empty<RealmVm>();

    public record AssignmentVm(Guid ClientGuid, string ClientId, string? ClientName, Guid RealmId, string RealmName, string TenantName, bool IsActive);
    public record ClientVm(Guid Id, string ClientId, string? ClientName, Guid RealmId);
    public record RealmVm(Guid Id, string Name);

    public async Task<IActionResult> OnGetAsync()
    {
        var userQuery = from u in db.Users.AsNoTracking()
                        join t in db.Tenants on u.TenantId equals t.Id
                        where u.Id == UserId
                        select new { User = u, Tenant = t };

        var userResult = await userQuery.FirstOrDefaultAsync();
        if (userResult is null) return RedirectToPage("/Admin/Users/Index");

        UserTenantId = userResult.User.TenantId;
        TenantName = userResult.Tenant.Name;
        SetHeading(userResult.User.Username, userResult.User.Name);

        // Load realms filtered by user's tenant
        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == UserTenantId)
            .OrderBy(r => r.Name)
            .Select(r => new RealmVm(r.Id, r.Name))
            .ToListAsync();

        // Load clients filtered by user's tenant and selected realm (if any)
        var clientQuery = db.Clients.AsNoTracking()
            .Where(c => c.TenantId == UserTenantId);

        if (RealmId.HasValue)
        {
            clientQuery = clientQuery.Where(c => c.RealmId == RealmId.Value);
        }

        Clients = await clientQuery
            .OrderBy(c => c.ClientId)
            .Select(c => new ClientVm(c.Id, c.ClientId, c.ClientName, c.RealmId))
            .ToListAsync();

        // Load assignments with tenant info
        Assignments = await db.UserClientAssignments.AsNoTracking()
            .Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.c.RealmId, r => r.Id, (ac, r) => new { ac.a, ac.c, r })
            .Join(db.Tenants, acr => acr.c.TenantId, t => t.Id, (acr, t) => new { acr.a, acr.c, acr.r, t })
            .OrderBy(x => x.c.ClientId)
            .Select(x => new AssignmentVm(
                x.c.Id,
                x.c.ClientId,
                x.c.ClientName,
                x.r.Id,
                x.r.Name,
                x.t.Name,
                x.a.IsActive))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (UserId == Guid.Empty || ClientId == Guid.Empty || !RealmId.HasValue)
        {
            return await OnGetAsync();
        }

        // Get user's tenant with tenant filtering
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var userQuery = db.Users.AsNoTracking().Where(u => u.Id == UserId);
        
        if (!isPlatformAdmin)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return RedirectToPage("/Admin/Users/Index");
            }
            userQuery = userQuery.Where(u => u.TenantId == currentTenantId.Value);
        }

        var user = await userQuery.FirstOrDefaultAsync();
        if (user is null) return RedirectToPage("/Admin/Users/Index");

        // Validate client belongs to user's tenant
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == ClientId && c.TenantId == user.TenantId);
        if (client is null)
        {
            // Client doesn't exist or doesn't belong to user's tenant - security violation
            return await OnGetAsync();
        }

        // Validate realm belongs to user's tenant
        var realmValid = await db.Realms.AsNoTracking()
            .AnyAsync(r => r.Id == RealmId.Value && r.TenantId == user.TenantId);
        if (!realmValid)
        {
            // Realm doesn't belong to user's tenant - security violation
            return await OnGetAsync();
        }

        // Validate client belongs to selected realm
        if (client.RealmId != RealmId.Value)
        {
            // Client doesn't belong to selected realm - invalid assignment
            return await OnGetAsync();
        }

        var exists = await db.UserClientAssignments.AnyAsync(a =>
            a.UserId == UserId &&
            a.ClientId == ClientId &&
            a.RealmId == RealmId.Value);

        if (!exists)
        {
            db.UserClientAssignments.Add(new UserClientAssignment
            {
                UserId = UserId,
                ClientId = ClientId,
                RealmId = RealmId.Value,
                IsActive = IsActive
            });
            await db.SaveChangesAsync();
        }

        return RedirectToPage(new { userId = UserId, realmId = RealmId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid clientId, Guid realmId)
    {
        var entity = await db.UserClientAssignments.FirstOrDefaultAsync(a =>
            a.UserId == UserId &&
            a.ClientId == clientId &&
            a.RealmId == realmId);

        if (entity is not null)
        {
            db.UserClientAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }

        return RedirectToPage(new { userId = UserId, realmId = RealmId });
    }
}
