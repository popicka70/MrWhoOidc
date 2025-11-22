using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IUserService userService,
    IPasswordHasher passwordHasher,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record UserRow(Guid Id, string Username, string? Email, string? Name, DateTimeOffset CreatedAt, Guid TenantId, string TenantName);

    public IReadOnlyList<UserRow> Users { get; private set; } = Array.Empty<UserRow>();
    public bool IsPlatformAdmin { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public async Task OnGetAsync()
    {
        // Check if user is platform admin
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;

        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            Users = Array.Empty<UserRow>();
            return;
        }

        // Build query with tenant JOIN
        var q = db.Users.AsNoTracking()
            .Where(u => u.TenantId == currentTenantId.Value)
            .Join(db.Tenants, u => u.TenantId, t => t.Id, (u, t) => new { User = u, Tenant = t });

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(x => x.User.Username.Contains(s) || (x.User.Email != null && x.User.Email.Contains(s)) || (x.User.Name != null && x.User.Name.Contains(s)));
        }

        Users = await q
            .OrderBy(x => x.User.Username)
            .Select(x => new UserRow(
                x.User.Id,
                x.User.Username,
                x.User.Email,
                x.User.Name,
                x.User.CreatedAt,
                x.User.TenantId,
                x.Tenant.Name
            ))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirectToPage();
        }

        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value);
        if (entity is null) return TenantAwareRedirectToPage();
        
        // Capture for cache invalidation
        var username = entity.Username;
        var tenantId = entity.TenantId;

        // Check if user is in use
        var inUse = await db.Tokens.AnyAsync(t => t.UserId == id)
            || await db.Consents.AnyAsync(c => c.UserId == id)
            || await db.UserClientAssignments.AnyAsync(a => a.UserId == id)
            || await db.UserRoleAssignments.AnyAsync(a => a.UserId == id);
        if (inUse)
        {
            TempData["Error"] = "Cannot delete user; it is referenced by tokens, consents, or assignments.";
            return TenantAwareRedirectToPage();
        }
        db.Users.Remove(entity);
        await db.SaveChangesAsync();
        
        // Invalidate user cache after deletion
        await userService.InvalidateUserCacheAsync(id, username, tenantId);
        
        return TenantAwareRedirect("/Admin/Users");
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(Guid id)
    {
        // Only platform admins can reset passwords
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        if (!platformAdminResult.Succeeded)
        {
            return Forbid();
        }

        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirectToPage();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value);
        if (user is null)
        {
            TempData["Error"] = "User not found.";
            return TenantAwareRedirectToPage();
        }

        // Generate a random temporary password
        var tempPassword = GenerateTemporaryPassword();
        user.PasswordHash = passwordHasher.Hash(tempPassword);
        user.HashAlgorithm = "argon2id";
        await db.SaveChangesAsync();

        // Invalidate user cache
        await userService.InvalidateUserCacheAsync(user.Id, user.Username, user.TenantId);

        TempData["Success"] = $"Password reset for user '<strong>{user.Username}</strong>'.<br/>Temporary password: <code class='user-select-all'>{tempPassword}</code><br/><small class='text-warning'><i class='bi bi-exclamation-triangle'></i> Please save this password and share it securely with the user.</small>";
        
        return TenantAwareRedirect("/Admin/Users");
    }

    private static string GenerateTemporaryPassword()
    {
        // Generate a secure random password: 16 characters, alphanumeric + symbols
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        var random = new Random();
        return new string(Enumerable.Range(0, 16).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
