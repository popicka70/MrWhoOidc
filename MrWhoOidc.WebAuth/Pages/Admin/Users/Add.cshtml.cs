using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
    IUserAccountProvisioner accountProvisioner,
    IWebHostEnvironment env) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    private readonly IUserAccountProvisioner _accountProvisioner = accountProvisioner;
    public class AddInput
    {
        [Required, StringLength(200)]
        public string Username { get; set; } = string.Empty;

        [EmailAddress, StringLength(256)]
        public string? Email { get; set; }

        [StringLength(200)]
        public string? Name { get; set; }
    }

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var currentTenant = TenantAccessor.CurrentTenant;
        if (currentTenant is null)
        {
            ModelState.AddModelError(string.Empty, "Unable to determine current tenant context.");
            return Page();
        }

        var username = Input.Username.Trim();

        // Check uniqueness within tenant
        if (await db.Users.AnyAsync(u => u.TenantId == currentTenant.TenantId && u.Username == username))
        {
            ModelState.AddModelError("Input.Username", "Username already exists in this tenant.");
            return Page();
        }

        var email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email!.Trim();
        var normalized = EmailNormalizer.NormalizeForLookup(email);
        if (!string.IsNullOrEmpty(normalized) && await db.Users.AnyAsync(u => u.TenantId == currentTenant.TenantId && u.NormalizedEmail == normalized))
        {
            ModelState.AddModelError("Input.Email", "Email already exists in this tenant.");
            return Page();
        }

        var user = new User
        {
            TenantId = currentTenant.TenantId,
            Username = username,
            Email = email,
            Name = Input.Name,
            EmailVerified = env.IsDevelopment() || env.IsStaging(),
            EmailVerifiedAt = (env.IsDevelopment() || env.IsStaging()) ? DateTimeOffset.UtcNow : null
        };

        db.Users.Add(user);

        await db.SaveChangesAsync();
        await _accountProvisioner.EnsureAsync(user, currentTenant.TenantId, defaultRealmId: null, isTenantAdmin: false, HttpContext.RequestAborted);
        return TenantAwareRedirect("/Admin/Users");
    }
}
