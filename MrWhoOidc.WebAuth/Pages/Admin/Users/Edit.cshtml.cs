using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize(Policy = "tenant-admin")]
public class EditModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IUserService userService,
    IMultiTenancyOptions multiTenancyOptions) : UserPageModelBase(tenantAccessor, multiTenancyOptions)
{
    public class EditInput
    {
        [Required, StringLength(200)]
        public string Username { get; set; } = string.Empty;
        [EmailAddress, StringLength(256)]
        public string? Email { get; set; }
        [StringLength(200)]
        public string? Name { get; set; }
    }

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public string TenantName { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Users");
        }

        var userQuery = from u in db.Users.AsNoTracking()
                        join t in db.Tenants on u.TenantId equals t.Id
                        where u.Id == id && u.TenantId == currentTenantId.Value
                        select new { User = u, Tenant = t };

        var result = await userQuery.FirstOrDefaultAsync();
        if (result is null) return TenantAwareRedirect("/Admin/Users");

        TenantName = result.Tenant.Name;
        Input = new EditInput
        {
            Username = result.User.Username,
            Email = result.User.Email,
            Name = result.User.Name
        };
        SetHeading(result.User.Username, result.User.Name);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (!ModelState.IsValid)
        {
            await LoadTenantNameAsync(id);
            return Page();
        }

        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return TenantAwareRedirect("/Admin/Users");
        }

        var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.TenantId == currentTenantId.Value);
        if (entity is null) return TenantAwareRedirect("/Admin/Users");

        // Load tenant name for display
        await LoadTenantNameAsync(id);

        // Initialize heading from current entity state for validation error scenarios.
        SetHeading(entity.Username, entity.Name);

        var newUsername = Input.Username.Trim();
        if (!string.Equals(entity.Username, newUsername, StringComparison.Ordinal))
        {
            // Username uniqueness within tenant
            var exists = await db.Users.AnyAsync(u => u.TenantId == entity.TenantId && u.Username == newUsername);
            if (exists)
            {
                ModelState.AddModelError("Input.Username", "Username already exists.");
                return Page();
            }
            entity.Username = newUsername;
        }

        var newEmail = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email!.Trim();
        var normalized = EmailNormalizer.NormalizeForLookup(newEmail);
        if (!string.Equals(entity.NormalizedEmail, normalized, StringComparison.Ordinal))
        {
            // Email uniqueness within tenant
            if (!string.IsNullOrEmpty(normalized) && await db.Users.AnyAsync(u => u.TenantId == entity.TenantId && u.NormalizedEmail == normalized && u.Id != id))
            {
                ModelState.AddModelError("Input.Email", "Email already exists.");
                return Page();
            }
            entity.Email = newEmail;
            entity.EmailVerified = false;
            entity.EmailVerifiedAt = null;
        }

        entity.Name = string.IsNullOrWhiteSpace(Input.Name) ? null : Input.Name.Trim();
        await db.SaveChangesAsync();

        // Invalidate user cache after update
        await userService.InvalidateUserCacheAsync(entity.Id, entity.Username, entity.TenantId);

        SetHeading(entity.Username, entity.Name);
        return TenantAwareRedirect("/Admin/Users");
    }

    private async Task LoadTenantNameAsync(Guid userId)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            TenantName = string.Empty;
            return;
        }

        TenantName = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.TenantId == currentTenantId.Value)
            .Join(db.Tenants, u => u.TenantId, t => t.Id, (u, t) => t.Name)
            .FirstOrDefaultAsync() ?? string.Empty;
    }
}
