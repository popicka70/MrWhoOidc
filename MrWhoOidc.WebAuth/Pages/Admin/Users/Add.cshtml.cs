using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

[Authorize(Policy = "tenant-admin")]
public class AddModel(
    AuthDbContext db, 
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    IUserAccountProvisioner accountProvisioner) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    private readonly IUserAccountProvisioner _accountProvisioner = accountProvisioner;
    public class AddInput
    {
        [Required]
        public Guid TenantId { get; set; }

        [Required, StringLength(200)]
        public string Username { get; set; } = string.Empty;

        [EmailAddress, StringLength(256)]
        public string? Email { get; set; }

        [StringLength(200)]
        public string? Name { get; set; }
    }

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public List<SelectListItem> TenantOptions { get; private set; } = new();

    public async Task OnGetAsync()
    {
        await LoadTenantsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadTenantsAsync();
            return Page();
        }

        // Validate tenant exists and is active
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == Input.TenantId && t.Status == TenantStatus.Active);
        if (!tenantExists)
        {
            ModelState.AddModelError("Input.TenantId", "Invalid tenant selected.");
            await LoadTenantsAsync();
            return Page();
        }

        var username = Input.Username.Trim();

        // Check uniqueness within tenant
        if (await db.Users.AnyAsync(u => u.TenantId == Input.TenantId && u.Username == username))
        {
            ModelState.AddModelError("Input.Username", "Username already exists in this tenant.");
            await LoadTenantsAsync();
            return Page();
        }

        var email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email!.Trim();
        var normalized = EmailNormalizer.NormalizeForLookup(email);
        if (!string.IsNullOrEmpty(normalized) && await db.Users.AnyAsync(u => u.TenantId == Input.TenantId && u.NormalizedEmail == normalized))
        {
            ModelState.AddModelError("Input.Email", "Email already exists in this tenant.");
            await LoadTenantsAsync();
            return Page();
        }

        var user = new User
        {
            TenantId = Input.TenantId,
            Username = username,
            Email = email,
            Name = Input.Name,
            EmailVerified = false,
            HashAlgorithm = "argon2id",
            PasswordHash = string.Empty
        };

        db.Users.Add(user);

        await db.SaveChangesAsync();
        await _accountProvisioner.EnsureAsync(user, Input.TenantId, defaultRealmId: null, isTenantAdmin: false, HttpContext.RequestAborted);
        return TenantAwareRedirect("/Admin/Users", new { TenantId = Input.TenantId });
    }

    private async Task LoadTenantsAsync()
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Name)
            .ToListAsync();
        TenantOptions = tenants.Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToList();
    }
}
