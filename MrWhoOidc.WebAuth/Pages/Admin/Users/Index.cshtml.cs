using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Observability;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IUserService userService,
    IPasswordHasher passwordHasher,
    IUserAccountService userAccountService,
    IUserAccountProvisioner accountProvisioner,
    GlobalAuthMetrics metrics,
    ILogger<IndexModel> logger,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public sealed record UserRow(Guid Id, string Username, string? Email, string? Name, DateTimeOffset CreatedAt, Guid TenantId, string TenantName, UserStatus Status);

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
                x.Tenant.Name,
                x.User.Status
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
        var blockers = new List<string>();
        if (await db.Tokens.AnyAsync(t => t.UserId == id)) blockers.Add("active tokens");
        if (await db.Consents.AnyAsync(c => c.UserId == id)) blockers.Add("consents");
        if (await db.UserClientAssignments.AnyAsync(a => a.UserId == id)) blockers.Add("client assignments");
        if (await db.UserRealmRoleAssignments.AnyAsync(a => a.UserId == id)) blockers.Add("realm role assignments");
        if (await db.UserClientRoleAssignments.AnyAsync(a => a.UserId == id)) blockers.Add("client role assignments");
        if (await db.ImpersonationAuditLogs.AnyAsync(a => a.PlatformAdminUserId == id)) blockers.Add("impersonation audit history");

        if (blockers.Count > 0)
        {
            var blockerList = string.Join(", ", blockers);
            logger.LogWarning(
                "Cannot delete user {UserId} (username {Username}) in tenant {TenantId}: still referenced by: {Blockers}",
                id, username, tenantId, blockerList);
            TempData["Error"] = $"Cannot delete user '{username}' because it is still referenced by: {blockerList}. Remove or reassign those records before deleting the user.";
            return TenantAwareRedirectToPage();
        }

        try
        {
            db.Users.Remove(entity);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex,
                "Unable to delete user {UserId} (username {Username}) in tenant {TenantId} due to a referential-integrity violation",
                id, username, tenantId);
            TempData["Error"] = $"Unable to delete user '{username}'. The user is still referenced by other records that could not be removed automatically. Remove or reassign those records and try again.";
            return TenantAwareRedirectToPage();
        }

        // Invalidate user cache after deletion
        await userService.InvalidateUserCacheAsync(id, username, tenantId);

        return TenantAwareRedirect("/Admin/Users");
    }

    public async Task<IActionResult> OnPostDeactivateAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirectToPage();
        }

        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value);
        if (entity is null) return TenantAwareRedirectToPage();

        if (entity.Status == UserStatus.Deactivated)
        {
            TempData["Error"] = $"User '{entity.Username}' is already deactivated.";
            return TenantAwareRedirectToPage();
        }

        entity.Status = UserStatus.Deactivated;
        entity.DeactivatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to deactivate user {UserId} in tenant {TenantId}", id, currentTenantId);
            TempData["Error"] = $"Unable to deactivate user '{entity.Username}'. Please try again.";
            return TenantAwareRedirectToPage();
        }

        logger.LogInformation("User {UserId} ({Username}) deactivated in tenant {TenantId}", id, entity.Username, currentTenantId);
        await userService.InvalidateUserCacheAsync(id, entity.Username, entity.TenantId);
        TempData["Success"] = $"User '{entity.Username}' has been deactivated.";
        return TenantAwareRedirect("/Admin/Users");
    }

    public async Task<IActionResult> OnPostReactivateAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirectToPage();
        }

        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value);
        if (entity is null) return TenantAwareRedirectToPage();

        if (entity.Status == UserStatus.Active)
        {
            TempData["Error"] = $"User '{entity.Username}' is already active.";
            return TenantAwareRedirectToPage();
        }

        entity.Status = UserStatus.Active;
        entity.DeactivatedAt = null;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to reactivate user {UserId} in tenant {TenantId}", id, currentTenantId);
            TempData["Error"] = $"Unable to reactivate user '{entity.Username}'. Please try again.";
            return TenantAwareRedirectToPage();
        }

        logger.LogInformation("User {UserId} ({Username}) reactivated in tenant {TenantId}", id, entity.Username, currentTenantId);
        await userService.InvalidateUserCacheAsync(id, entity.Username, entity.TenantId);
        TempData["Success"] = $"User '{entity.Username}' has been reactivated.";
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

        // Find the per-tenant User
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value);

        if (user is null)
        {
            TempData["Error"] = "User not found.";
            return TenantAwareRedirectToPage();
        }

        // Find the linked UserAccount via email
        UserAccount? userAccount = null;
        if (!string.IsNullOrEmpty(user.Email))
        {
            userAccount = await userAccountService.FindByEmailAsync(user.Email);
        }

        if (userAccount is null)
        {
            // User not linked to UserAccount - provision one now
            logger.LogWarning("⚠️ [Admin Reset] User {UserId} not linked to UserAccount, provisioning now",
                user.Id);

            await accountProvisioner.EnsureAsync(user, user.TenantId, null, false, HttpContext.RequestAborted);
            userAccount = await userAccountService.FindByEmailAsync(user.Email!);

            if (userAccount is null)
            {
                TempData["Error"] = $"Failed to provision global account for user '{user.Username}'.";
                return TenantAwareRedirect("/Admin/Users");
            }
        }

        // Get affected tenant count for audit logging
        var affectedTenantCount = await db.UserTenantMemberships
            .Where(m => m.UserAccountId == userAccount.Id && m.Status == TenantMembershipStatus.Active)
            .CountAsync();

        // Generate a random temporary password and update global UserAccount
        var globalTempPassword = CryptoHelper.GenerateSecureRandomString(16);
        await userAccountService.UpdatePasswordAsync(
            userAccount.Id,
            passwordHasher.Hash(globalTempPassword),
            null,
            "argon2id");

        // Clear any lockout state
        await userAccountService.UpdateLockoutAsync(
            userAccount.Id,
            failedAttempts: 0,
            lastFailedAt: null,
            lockedOutUntil: null);

        // Record metrics
        metrics.AdminPasswordReset(affectedTenantCount);

        // Audit logging with PII hashing
        var adminUsername = User.Identity?.Name ?? "unknown";
        logger.LogInformation(
            "🔐 [Admin Reset] Admin {AdminHash} reset password for UserAccount {AccountId}. " +
            "Affected tenants: {TenantCount}. Target user: {UserHash}",
            HashForLog(adminUsername),
            userAccount.Id,
            affectedTenantCount,
            HashForLog(user.Username));

        // Invalidate user cache
        await userService.InvalidateUserCacheAsync(user.Id, user.Username, user.TenantId);

        TempData["ResetPassword_Username"] = user.Username;
        TempData["ResetPassword_TempPassword"] = globalTempPassword;
        TempData["ResetPassword_AffectedTenantCount"] = affectedTenantCount.ToString();

        return TenantAwareRedirect("/Admin/Users");
    }

    private static string HashForLog(string value)
    {
        return CryptoHelper.ComputeSha256Hex(value)[..8]; // First 8 chars of SHA256
    }
}
