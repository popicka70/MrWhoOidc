using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users.Linked;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService) : UserPageModelBase(tenantAccessor)
{
    [FromRoute]
    public Guid UserId { get; set; }

    public IReadOnlyList<ExternalIdentity> Items { get; private set; } = Array.Empty<ExternalIdentity>();

    private async Task<User?> GetUserWithTenantFilterAsync()
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var userQuery = db.Users.AsNoTracking().Where(u => u.Id == UserId);
        
        if (!isPlatformAdmin)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue)
            {
                return null;
            }
            userQuery = userQuery.Where(u => u.TenantId == currentTenantId.Value);
        }

        return await userQuery.FirstOrDefaultAsync();
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await GetUserWithTenantFilterAsync();
        if (user is null) return RedirectToPage("/Admin/Users/Index");
        SetHeading(user.Username, user.Name);
        Items = await db.ExternalIdentities.AsNoTracking()
            .Where(e => e.UserId == UserId)
            .OrderBy(e => e.ProviderName).ThenBy(e => e.Issuer).ThenBy(e => e.Subject)
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid linkId)
    {
        var entity = await db.ExternalIdentities.FirstOrDefaultAsync(e => e.Id == linkId && e.UserId == UserId);
        if (entity is not null)
        {
            db.ExternalIdentities.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { userId = UserId });
    }

}
