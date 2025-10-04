using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants;

[Authorize(Policy = "platform-admin")]
public partial class CreateModel(
    AuthDbContext db,
    IPasswordHasher hasher,
    IOptions<MultiTenancyOptions> multiTenancyOptions,
    IHttpContextAccessor httpContextAccessor) : PageModel
{
    [BindProperty]
    public TenantInput Input { get; set; } = new();

    public class TenantInput
    {
        [Required]
        [MaxLength(100)]
        [RegularExpression(@"^[a-z0-9\-]+$", ErrorMessage = "Slug must contain only lowercase letters, numbers, and hyphens")]
        public string Slug { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Description { get; set; }

        [Required]
        [EmailAddress]
        [MaxLength(256)]
        public string AdminEmail { get; set; } = string.Empty;

        [Required]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters")]
        public string AdminPassword { get; set; } = string.Empty;

        public TenantStatus Status { get; set; } = TenantStatus.Active;

        [Range(0, int.MaxValue)]
        public int MaxUsers { get; set; } = 10000;

        [Range(0, int.MaxValue)]
        public int MaxClients { get; set; } = 100;

        [MaxLength(200)]
        [Url]
        public string? LogoUrl { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Must be a valid hex color (e.g., #0d6efd)")]
        public string? PrimaryColor { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Must be a valid hex color (e.g., #6610f2)")]
        public string? AccentColor { get; set; }

        [MaxLength(100)]
        public string? BillingPlan { get; set; }
    }

    public void OnGet()
    {
        // Set defaults
        Input.Status = TenantStatus.Active;
        Input.MaxUsers = 10000;
        Input.MaxClients = 100;
        Input.BillingPlan = "Free";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Validate slug uniqueness
        if (await db.Tenants.AnyAsync(t => t.Slug == Input.Slug))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Slug)}", "A tenant with this slug already exists.");
            return Page();
        }

        // Validate email uniqueness (across all tenants in raw DB - not tenant-scoped)
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(Input.AdminEmail);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.AdminEmail)}", "This email is already in use.");
            return Page();
        }

        // Build issuer URI based on multi-tenancy mode
        var baseUrl = $"{httpContextAccessor.HttpContext!.Request.Scheme}://{httpContextAccessor.HttpContext.Request.Host}";
        var issuerUri = multiTenancyOptions.Value.Enabled
            ? $"{baseUrl}/t/{Input.Slug}"
            : baseUrl;

        // Create tenant
        var tenant = new Tenant
        {
            Slug = Input.Slug,
            Name = Input.Name,
            Description = Input.Description,
            IssuerUri = issuerUri,
            Status = Input.Status,
            MaxUsers = Input.MaxUsers,
            MaxClients = Input.MaxClients,
            LogoUrl = Input.LogoUrl,
            PrimaryColor = Input.PrimaryColor,
            AccentColor = Input.AccentColor,
            AdminEmail = Input.AdminEmail,
            BillingPlan = Input.BillingPlan,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Create default realm for tenant
        var realm = new Realm
        {
            TenantId = tenant.Id,
            Name = "default",
            DisplayName = $"{Input.Name} Default Realm"
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync();

        // Create first admin user
        var passwordHash = hasher.Hash(Input.AdminPassword);
        var adminUser = new User
        {
            TenantId = tenant.Id,
            Username = Input.AdminEmail.Split('@')[0], // Use email prefix as username
            Email = Input.AdminEmail,
            NormalizedEmail = normalizedEmail,
            Name = "Admin User",
            PasswordHash = passwordHash,
            EmailVerified = true, // Auto-verify first admin
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Users.Add(adminUser);
        await db.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Tenant '{Input.Name}' created successfully! Admin user: {Input.AdminEmail}";
        return RedirectToPage("Index");
    }

    // Source generator for regex
    [GeneratedRegex(@"^[a-z0-9\-]+$")]
    private static partial Regex SlugPattern();
}
