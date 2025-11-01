using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Roles;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    ILogger<IndexModel> logger,
    IMultiTenancyOptions multiTenancyOptions) : UserPageModelBase(tenantAccessor, multiTenancyOptions)
{
    [FromRoute]
    public Guid UserId { get; set; }

    // Binds from ?realm=guid on GET and name="SelectedRealmId" on POST
    [BindProperty(SupportsGet = true)]
    public Guid? SelectedRealmId { get; set; }

    // Binds from ?client=guid on GET and name="SelectedClientId" on POST
    [BindProperty(SupportsGet = true)]
    public Guid? SelectedClientId { get; set; }

    // Add (realm-role)
    [BindProperty]
    public Guid RealmAddRoleId { get; set; }

    // Add (client-role)
    [BindProperty]
    public Guid ClientAddRoleId { get; set; }

    public string TenantName { get; set; } = string.Empty;
    public Guid UserTenantId { get; set; }

    public IReadOnlyList<RealmAssignmentVm> RealmAssignments { get; private set; } = Array.Empty<RealmAssignmentVm>();
    public IReadOnlyList<ClientAssignmentVm> ClientAssignments { get; private set; } = Array.Empty<ClientAssignmentVm>();

    public IReadOnlyList<ClientVm> Clients { get; private set; } = Array.Empty<ClientVm>();
    public IReadOnlyList<RealmVm> Realms { get; private set; } = Array.Empty<RealmVm>();
    public IReadOnlyList<RoleVm> Roles { get; private set; } = Array.Empty<RoleVm>();

    public record RealmAssignmentVm(Guid RoleId, Guid RealmId, string RealmName, string RoleName, string TenantName, bool IsActive);
    public record ClientAssignmentVm(Guid RoleId, Guid ClientGuid, string ClientId, string? ClientName, Guid RealmId, string RealmName, string RoleName, string TenantName, bool IsActive);
    public record ClientVm(Guid Id, string ClientId, string? ClientName);
    public record RoleVm(Guid Id, string Name, string RealmName, Guid RealmId);
    public record RealmVm(Guid Id, string Name);

    public async Task<IActionResult> OnGetAsync()
    {
        logger.LogInformation(
            "OnGetAsync called: UserId={UserId}, SelectedRealmId={SelectedRealmId}, SelectedClientId={SelectedClientId}",
            UserId, SelectedRealmId, SelectedClientId);

        var userQuery = from u in db.Users.AsNoTracking()
                        join t in db.Tenants on u.TenantId equals t.Id
                        where u.Id == UserId
                        select new { User = u, Tenant = t };

        var userResult = await userQuery.FirstOrDefaultAsync();
        if (userResult is null) return RedirectToPage("/admin/users");

        UserTenantId = userResult.User.TenantId;
        TenantName = userResult.Tenant.Name;
        SetHeading(userResult.User.Username, userResult.User.Name);

        // Realms filtered by user's tenant
        Realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == UserTenantId)
            .OrderBy(r => r.Name)
            .Select(r => new RealmVm(r.Id, r.Name))
            .ToListAsync();

        // Clients filtered by user's tenant
        Clients = await db.Clients.AsNoTracking()
            .Where(c => c.TenantId == UserTenantId)
            .OrderBy(c => c.ClientId)
            .Select(c => new ClientVm(c.Id, c.ClientId, c.ClientName))
            .ToListAsync();

        // Roles filtered by user's tenant with their realm name
        Roles = await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == UserTenantId)
            .Join(db.Realms, r => r.RealmId, rl => rl.Id, (r, rl) => new { RoleId = r.Id, r.Name, RealmName = rl.Name, RealmId = rl.Id })
            .OrderBy(x => x.Name)
            .Select(x => new RoleVm(x.RoleId, x.Name, x.RealmName, x.RealmId))
            .ToListAsync();

        // Realm-role assignments with tenant info
        RealmAssignments = await db.UserRealmRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == UserId)
            .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Join(db.Realms, ar => ar.a.RealmId, rl => rl.Id, (ar, rl) => new { ar, rl })
            .Join(db.Tenants, arrl => arrl.ar.r.TenantId, t => t.Id, (arrl, t) => new { arrl.ar, arrl.rl, t })
            .OrderBy(x => x.rl.Name).ThenBy(x => x.ar.r.Name)
            .Select(x => new RealmAssignmentVm(x.ar.r.Id, x.rl.Id, x.rl.Name, x.ar.r.Name, x.t.Name, x.ar.a.IsActive))
            .ToListAsync();

        // Client-role assignments with tenant info
        ClientAssignments = await db.UserClientRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == UserId)
            .Join(db.Clients, a => a.ClientId, c => c.Id, (a, c) => new { a, c })
            .Join(db.Realms, ac => ac.c.RealmId, rl => rl.Id, (ac, rl) => new { ac, rl })
            .Join(db.Roles, acr => acr.ac.a.RoleId, ro => ro.Id, (acr, ro) => new { acr, ro })
            .Join(db.Tenants, acrro => acrro.acr.ac.c.TenantId, t => t.Id, (acrro, t) => new { acrro.acr, acrro.ro, t })
            .OrderBy(x => x.acr.ac.c.ClientId).ThenBy(x => x.ro.Name)
            .Select(x => new ClientAssignmentVm(x.ro.Id, x.acr.ac.c.Id, x.acr.ac.c.ClientId, x.acr.ac.c.ClientName, x.acr.rl.Id, x.acr.rl.Name, x.ro.Name, x.t.Name, x.acr.ac.a.IsActive))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAddRealmAsync()
    {
        logger.LogInformation(
            "OnPostAddRealmAsync called: UserId={UserId}, SelectedRealmId={SelectedRealmId}, RealmAddRoleId={RealmAddRoleId}",
            UserId, SelectedRealmId, RealmAddRoleId);

        if (UserId == Guid.Empty || !SelectedRealmId.HasValue || RealmAddRoleId == Guid.Empty)
        {
            logger.LogWarning(
                "OnPostAddRealmAsync validation failed: UserId={UserId}, SelectedRealmId={SelectedRealmId}, RealmAddRoleId={RealmAddRoleId}",
                UserId, SelectedRealmId, RealmAddRoleId);
            return RedirectToPage(new { userId = UserId, SelectedRealmId });
        }

        // Get user's tenant with tenant filtering
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        logger.LogInformation("User isPlatformAdmin: {IsPlatformAdmin}", isPlatformAdmin);

        var userQuery = db.Users.AsNoTracking().Where(u => u.Id == UserId);
        
        if (!isPlatformAdmin)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            logger.LogInformation("Current tenant from TenantAccessor: {CurrentTenantId}", currentTenantId);
            
            if (!currentTenantId.HasValue)
            {
                logger.LogWarning("No current tenant found in TenantAccessor, redirecting to Users/Index");
                return RedirectToPage("/admin/users");
            }
            userQuery = userQuery.Where(u => u.TenantId == currentTenantId.Value);
        }

        var user = await userQuery.FirstOrDefaultAsync();
        if (user is null)
        {
            logger.LogWarning("User {UserId} not found or access denied", UserId);
            return RedirectToPage("/admin/users");
        }

        logger.LogInformation("User found: {UserId}, UserTenantId={UserTenantId}", user.Id, user.TenantId);

        // Validate realm belongs to user's tenant
        var realmValid = await db.Realms.AsNoTracking()
            .AnyAsync(r => r.Id == SelectedRealmId.Value && r.TenantId == user.TenantId);
        
        logger.LogInformation(
            "Realm validation: RealmId={RealmId}, UserTenantId={UserTenantId}, IsValid={IsValid}",
            SelectedRealmId.Value, user.TenantId, realmValid);
        
        if (!realmValid)
        {
            logger.LogWarning("Realm {RealmId} is not valid for user tenant {UserTenantId}", SelectedRealmId.Value, user.TenantId);
            return RedirectToPage(new { userId = UserId, SelectedRealmId });
        }

        // Validate role belongs to user's tenant AND the selected realm
        var roleValid = await db.Roles.AsNoTracking()
            .AnyAsync(r => r.Id == RealmAddRoleId && r.TenantId == user.TenantId && r.RealmId == SelectedRealmId.Value);
        
        logger.LogInformation(
            "Role validation: RoleId={RoleId}, RealmId={RealmId}, UserTenantId={UserTenantId}, IsValid={IsValid}",
            RealmAddRoleId, SelectedRealmId.Value, user.TenantId, roleValid);
        
        if (!roleValid)
        {
            logger.LogWarning(
                "Role {RoleId} is not valid for realm {RealmId} and tenant {UserTenantId}",
                RealmAddRoleId, SelectedRealmId.Value, user.TenantId);
            return RedirectToPage(new { userId = UserId, SelectedRealmId });
        }

        var exists = await db.UserRealmRoleAssignments.AnyAsync(a =>
            a.UserId == UserId &&
            a.RealmId == SelectedRealmId.Value &&
            a.RoleId == RealmAddRoleId);

        logger.LogInformation("Assignment already exists: {Exists}", exists);

        if (!exists)
        {
            db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
            {
                UserId = UserId,
                RealmId = SelectedRealmId.Value,
                RoleId = RealmAddRoleId,
                IsActive = true
            });
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Successfully added role assignment: UserId={UserId}, RealmId={RealmId}, RoleId={RoleId}",
                UserId, SelectedRealmId.Value, RealmAddRoleId);
        }
        else
        {
            logger.LogInformation("Role assignment already exists, skipping creation");
        }
        
        logger.LogInformation("Redirecting to: userId={UserId}, SelectedRealmId={RealmId}", UserId, SelectedRealmId);
        return RedirectToPage(new { userId = UserId, SelectedRealmId });
    }

    public async Task<IActionResult> OnPostDeleteRealmAsync(Guid roleId, Guid realmId)
    {
        var entity = await db.UserRealmRoleAssignments.FirstOrDefaultAsync(a =>
            a.UserId == UserId &&
            a.RealmId == realmId &&
            a.RoleId == roleId);

        if (entity is not null)
        {
            db.UserRealmRoleAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId, SelectedRealmId = realmId });
    }

    public async Task<IActionResult> OnPostAddClientAsync()
    {
        if (UserId == Guid.Empty || !SelectedClientId.HasValue || ClientAddRoleId == Guid.Empty)
            return RedirectToPage(new { userId = UserId, SelectedClientId });

        // Get user's tenant with tenant filtering
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var userQuery = db.Users.AsNoTracking().Where(u => u.Id == UserId);
        
        if (!isPlatformAdmin)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return RedirectToPage("/admin/users");
            }
            userQuery = userQuery.Where(u => u.TenantId == currentTenantId.Value);
        }

        var user = await userQuery.FirstOrDefaultAsync();
        if (user is null) return RedirectToPage("/admin/users");

        // Validate client belongs to user's tenant
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == SelectedClientId.Value && c.TenantId == user.TenantId);
        if (client is null)
        {
            return RedirectToPage(new { userId = UserId, SelectedClientId });
        }

        // Validate role belongs to user's tenant
        var roleValid = await db.Roles.AsNoTracking()
            .AnyAsync(r => r.Id == ClientAddRoleId && r.TenantId == user.TenantId);
        if (!roleValid)
        {
            return RedirectToPage(new { userId = UserId, SelectedClientId });
        }

        var exists = await db.UserClientRoleAssignments.AnyAsync(a =>
            a.UserId == UserId &&
            a.ClientId == SelectedClientId.Value &&
            a.RoleId == ClientAddRoleId);

        if (!exists)
        {
            db.UserClientRoleAssignments.Add(new UserClientRoleAssignment
            {
                UserId = UserId,
                ClientId = SelectedClientId.Value,
                RoleId = ClientAddRoleId,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId, SelectedClientId });
    }

    public async Task<IActionResult> OnPostDeleteClientAsync(Guid roleId, Guid clientId)
    {
        var entity = await db.UserClientRoleAssignments.FirstOrDefaultAsync(a =>
            a.UserId == UserId &&
            a.ClientId == clientId &&
            a.RoleId == roleId);

        if (entity is not null)
        {
            db.UserClientRoleAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId, SelectedClientId = clientId });
    }
}
