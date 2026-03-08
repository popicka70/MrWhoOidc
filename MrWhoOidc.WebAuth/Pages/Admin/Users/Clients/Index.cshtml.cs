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
    IAuthorizationService authorizationService,
    IMultiTenancyOptions multiTenancyOptions) : UserPageModelBase(tenantAccessor, multiTenancyOptions)
{
    [FromRoute]
    public Guid UserId { get; set; }

    public string TenantName { get; set; } = string.Empty;
    public Guid UserTenantId { get; set; }

    // Dual-list view models
    public List<ClientAssignmentViewModel> AvailableClients { get; private set; } = new();
    public List<ClientAssignmentViewModel> AssignedClients { get; private set; } = new();

    public record ClientAssignmentViewModel
    {
        public Guid Id { get; init; }
        public string ClientId { get; init; } = string.Empty;
        public string? ClientName { get; init; }
        public Guid RealmId { get; init; }
        public string RealmName { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var userQuery = from u in db.Users.AsNoTracking()
                        join t in db.Tenants on u.TenantId equals t.Id
                        where u.Id == UserId
                        select new { User = u, Tenant = t };

        var userResult = await userQuery.FirstOrDefaultAsync();
        if (userResult is null) return RedirectToPage("/admin/users");

        UserTenantId = userResult.User.TenantId;
        TenantName = userResult.Tenant.Name;
        SetHeading(userResult.User.Username, userResult.User.Name);

        await LoadClientAssignmentsAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(Guid clientId)
    {
        // Validate user access
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

        UserTenantId = user.TenantId;

        // Get client details and validate it belongs to user's tenant
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == user.TenantId);
        if (client is null)
        {
            return RedirectToPage(new { userId = UserId });
        }

        // Check if assignment already exists
        var exists = await db.UserClientAssignments.AnyAsync(a =>
            a.UserId == UserId &&
            a.ClientId == clientId &&
            a.RealmId == client.RealmId);

        if (!exists)
        {
            db.UserClientAssignments.Add(new UserClientAssignment
            {
                UserId = UserId,
                ClientId = clientId,
                RealmId = client.RealmId,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        return RedirectToPage(new { userId = UserId });
    }

    public async Task<IActionResult> OnPostUnassignAsync(Guid clientId)
    {
        // Validate user access
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

        // Get client to find realm
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == user.TenantId);
        if (client is null)
        {
            return RedirectToPage(new { userId = UserId });
        }

        // Remove the assignment
        var entity = await db.UserClientAssignments.FirstOrDefaultAsync(a =>
            a.UserId == UserId &&
            a.ClientId == clientId &&
            a.RealmId == client.RealmId);

        if (entity is not null)
        {
            db.UserClientAssignments.Remove(entity);
            await db.SaveChangesAsync();
        }

        return RedirectToPage(new { userId = UserId });
    }

    private async Task LoadClientAssignmentsAsync()
    {
        // Get all assigned client IDs for this user
        var assignedClientIds = await db.UserClientAssignments
            .AsNoTracking()
            .Where(a => a.UserId == UserId)
            .Select(a => new { a.ClientId, a.IsActive })
            .ToListAsync();

        var assignedClientIdSet = assignedClientIds.Select(a => a.ClientId).ToHashSet();

        // Get assigned clients with realm info
        AssignedClients = await db.Clients
            .AsNoTracking()
            .Where(c => c.TenantId == UserTenantId && assignedClientIdSet.Contains(c.Id))
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { Client = c, Realm = r })
            .Select(x => new ClientAssignmentViewModel
            {
                Id = x.Client.Id,
                ClientId = x.Client.ClientId,
                ClientName = x.Client.ClientName,
                RealmId = x.Realm.Id,
                RealmName = x.Realm.Name,
                IsActive = true // Will be updated below
            })
            .ToListAsync();

        // Update IsActive flag
        var assignmentLookup = assignedClientIds.ToDictionary(a => a.ClientId, a => a.IsActive);
        AssignedClients = AssignedClients.Select(c => c with
        {
            IsActive = assignmentLookup.TryGetValue(c.Id, out var isActive) && isActive
        }).ToList();

        // Get available clients (not assigned, same tenant)
        AvailableClients = await db.Clients
            .AsNoTracking()
            .Where(c => c.TenantId == UserTenantId && !assignedClientIdSet.Contains(c.Id))
            .Join(db.Realms, c => c.RealmId, r => r.Id, (c, r) => new { Client = c, Realm = r })
            .Select(x => new ClientAssignmentViewModel
            {
                Id = x.Client.Id,
                ClientId = x.Client.ClientId,
                ClientName = x.Client.ClientName,
                RealmId = x.Realm.Id,
                RealmName = x.Realm.Name,
                IsActive = true
            })
            .ToListAsync();
    }
}
