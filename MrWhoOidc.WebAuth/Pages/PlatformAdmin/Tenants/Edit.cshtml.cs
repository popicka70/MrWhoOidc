using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Security.Admin;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class EditModel(
    AuthDbContext db, 
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    [BindProperty]
    public TenantInput Input { get; set; } = new();

    public int CurrentUserCount { get; private set; }

    public int CurrentClientCount { get; private set; }

    public int CurrentIdPCount { get; private set; }

    public List<SelectListItem> DynamicClientRegistrationRealmOptions { get; private set; } = new();

    /// <summary>
    /// Current tenant slug for building tenant-aware API URLs
    /// </summary>
    public string CurrentTenantSlug => tenantAccessor.CurrentTenant?.Slug ?? "default";

    /// <summary>
    /// Indicates whether the tenant has an uploaded icon (not just a URL)
    /// </summary>
    public bool HasUploadedIcon => Input.TenantIconId.HasValue;

    public class TenantInput
    {
        public Guid Id { get; set; }

        public string Slug { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Issuer URI is required")]
        [RegularExpression(@"^https?://[^\s]+$", ErrorMessage = "Issuer URI must be a valid URI")]
        [MaxLength(500)]
        public string IssuerUri { get; set; } = string.Empty;

        public TenantStatus Status { get; set; }

        [Range(0, int.MaxValue)]
        public int MaxUsers { get; set; }

        [Range(0, int.MaxValue)]
        public int MaxClients { get; set; }

        [Range(0, int.MaxValue)]
        public int MaxIdentityProviders { get; set; }

        [MaxLength(200)]
        [Url]
        public string? LogoUrl { get; set; }

        /// <summary>
        /// Reference to uploaded tenant icon (takes precedence over LogoUrl)
        /// </summary>
        public Guid? TenantIconId { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Must be a valid hex color")]
        public string? PrimaryColor { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Must be a valid hex color")]
        public string? AccentColor { get; set; }

        [EmailAddress]
        [MaxLength(256)]
        public string? AdminEmail { get; set; }

        [MaxLength(100)]
        public string? BillingPlan { get; set; }

        /// <summary>
        /// License mode: InheritPlatform or Sublicense
        /// </summary>
        public TenantLicenseMode LicenseMode { get; set; } = TenantLicenseMode.InheritPlatform;

        /// <summary>
        /// Realm that will be assigned to dynamically registered clients (RFC 7591).
        /// Null disables dynamic client registration for this tenant.
        /// </summary>
        public Guid? DynamicClientRegistrationRealmId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? SuspendedAt { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        // Multi-tenancy must be enabled by license
        if (!multiTenancyOptions.Enabled)
        {
            return RedirectToPage("/PlatformAdmin/Index");
        }

        var tenant = await db.Tenants.FindAsync(id);
        if (tenant == null)
        {
            return NotFound();
        }

        await LoadTenantAsync(tenant);
        await LoadRealmOptionsAsync(tenant.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Multi-tenancy must be enabled by license
        if (!multiTenancyOptions.Enabled)
        {
            return RedirectToPage("/PlatformAdmin/Index");
        }

        if (!ModelState.IsValid)
        {
            await LoadCountsAsync(Input.Id);
            await LoadRealmOptionsAsync(Input.Id);
            return Page();
        }

        var tenant = await db.Tenants.FindAsync(Input.Id);
        if (tenant == null)
        {
            return NotFound();
        }

        await LoadRealmOptionsAsync(tenant.Id);

        if (Input.DynamicClientRegistrationRealmId != null)
        {
            var realmExists = await db.Realms.AnyAsync(r => r.TenantId == tenant.Id && r.Id == Input.DynamicClientRegistrationRealmId.Value);
            if (!realmExists)
            {
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.DynamicClientRegistrationRealmId)}", "Selected realm does not exist for this tenant.");
                await LoadCountsAsync(Input.Id);
                return Page();
            }
        }

        // Update tenant properties
        tenant.Name = Input.Name;
        tenant.Description = Input.Description;
        tenant.IssuerUri = Input.IssuerUri?.Trim() ?? string.Empty;
        tenant.Status = Input.Status;
        tenant.MaxUsers = Input.MaxUsers;
        tenant.MaxClients = Input.MaxClients;
        tenant.MaxIdentityProviders = Input.MaxIdentityProviders;
        tenant.LogoUrl = Input.LogoUrl;
        tenant.PrimaryColor = Input.PrimaryColor;
        tenant.AccentColor = Input.AccentColor;

        try
        {
            tenant.AdminEmail = NormalizeAdminEmail(Input.AdminEmail);
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.AdminEmail)}", ex.Message);
            await LoadCountsAsync(Input.Id);
            return Page();
        }

        tenant.BillingPlan = Input.BillingPlan;
        tenant.LicenseMode = Input.LicenseMode;

        // Update tenant SettingsJson (dynamic client registration realm)
        tenant.SettingsJson = UpsertDynamicRegistrationRealm(tenant.SettingsJson, Input.DynamicClientRegistrationRealmId);

        // Update status timestamps
        if (Input.Status == TenantStatus.Suspended && tenant.SuspendedAt == null)
        {
            tenant.SuspendedAt = DateTimeOffset.UtcNow;
        }
        else if (Input.Status == TenantStatus.Active)
        {
            tenant.SuspendedAt = null;
        }

        await db.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Tenant '{tenant.Name}' updated successfully.";
        return RedirectToPage("Index");
    }

    private static string? NormalizeAdminEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return EmailNormalizer.FormatForStorage(email, required: true, out _);
    }
    public async Task<IActionResult> OnPostSuspendAsync()
    {
        var tenant = await db.Tenants.FindAsync(Input.Id);
        if (tenant == null)
        {
            return NotFound();
        }

        if (tenant.Status == TenantStatus.Suspended)
        {
            // Activate
            tenant.Status = TenantStatus.Active;
            tenant.SuspendedAt = null;
            TempData["SuccessMessage"] = $"Tenant '{tenant.Name}' activated successfully.";
        }
        else
        {
            // Suspend
            tenant.Status = TenantStatus.Suspended;
            tenant.SuspendedAt = DateTimeOffset.UtcNow;
            TempData["SuccessMessage"] = $"Tenant '{tenant.Name}' suspended successfully.";
        }

        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var tenant = await db.Tenants.FindAsync(Input.Id);
        if (tenant == null)
        {
            return NotFound();
        }

        // Verify no users exist
        var userCount = await db.Users.CountAsync(u => u.TenantId == tenant.Id);
        if (userCount > 0)
        {
            TempData["ErrorMessage"] = "Cannot delete tenant with existing users. Please remove all users first.";
            return RedirectToPage("Edit", new { id = tenant.Id });
        }

        // Soft delete
        tenant.Status = TenantStatus.Deleted;
        tenant.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Tenant '{tenant.Name}' deleted successfully.";
        return RedirectToPage("Index");
    }

    private async Task LoadTenantAsync(Tenant tenant)
    {
        var dynamicRealmId = TryReadDynamicRegistrationRealmId(tenant.SettingsJson);

        Input = new TenantInput
        {
            Id = tenant.Id,
            Slug = tenant.Slug,
            Name = tenant.Name,
            Description = tenant.Description,
            IssuerUri = tenant.IssuerUri,
            Status = tenant.Status,
            MaxUsers = tenant.MaxUsers,
            MaxClients = tenant.MaxClients,
            MaxIdentityProviders = tenant.MaxIdentityProviders,
            LogoUrl = tenant.LogoUrl,
            TenantIconId = tenant.TenantIconId,
            PrimaryColor = tenant.PrimaryColor,
            AccentColor = tenant.AccentColor,
            AdminEmail = tenant.AdminEmail,
            BillingPlan = tenant.BillingPlan,
            LicenseMode = tenant.LicenseMode,
            DynamicClientRegistrationRealmId = dynamicRealmId,
            CreatedAt = tenant.CreatedAt,
            SuspendedAt = tenant.SuspendedAt,
            DeletedAt = tenant.DeletedAt
        };

        await LoadCountsAsync(tenant.Id);
    }

    private async Task LoadCountsAsync(Guid tenantId)
    {
        CurrentUserCount = await db.Users.CountAsync(u => u.TenantId == tenantId);
        CurrentClientCount = await db.Clients.CountAsync(c => c.TenantId == tenantId);
        CurrentIdPCount = await db.IdentityProviders.CountAsync(i => i.TenantId == tenantId);
    }

    private async Task LoadRealmOptionsAsync(Guid tenantId)
    {
        var realms = await db.Realms
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.DisplayName ?? r.Name)
            .Select(r => new { r.Id, r.Name, r.DisplayName })
            .ToListAsync();

        var options = new List<SelectListItem>
        {
            new SelectListItem { Value = "", Text = "Disabled (no realm)" }
        };

        foreach (var realm in realms)
        {
            options.Add(new SelectListItem
            {
                Value = realm.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(realm.DisplayName) ? realm.Name : $"{realm.DisplayName} ({realm.Name})"
            });
        }

        DynamicClientRegistrationRealmOptions = options;
    }

    private static Guid? TryReadDynamicRegistrationRealmId(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<TenantSettings>(settingsJson);
            return settings?.Auth?.DynamicClientRegistrationRealmId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? UpsertDynamicRegistrationRealm(string? settingsJson, Guid? realmId)
    {
        TenantSettings settings;

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            settings = new TenantSettings();
        }
        else
        {
            try
            {
                settings = JsonSerializer.Deserialize<TenantSettings>(settingsJson) ?? new TenantSettings();
            }
            catch (JsonException)
            {
                settings = new TenantSettings();
            }
        }

        settings.Auth ??= new AuthTenantSettings();
        settings.Auth.DynamicClientRegistrationRealmId = realmId;

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }
}
