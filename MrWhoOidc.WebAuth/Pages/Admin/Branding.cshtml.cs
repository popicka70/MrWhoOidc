using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.WebAuth.Pages.Admin;

[Authorize]
public class BrandingModel : PageModel
{
    private readonly AuthDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMultiTenancyOptions _multiTenancyOptions;
    private readonly ILogger<BrandingModel> _logger;

    public BrandingModel(
        AuthDbContext db,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        ILogger<BrandingModel> logger)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _multiTenancyOptions = multiTenancyOptions;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public string TenantSlug { get; set; } = string.Empty;

    [BindProperty]
    public BrandingInput Input { get; set; } = new();

    public bool IsMultiTenantMode { get; set; }
    public string? SuccessMessage { get; set; }

    public class BrandingInput
    {
        [MaxLength(200)]
        [Url(ErrorMessage = "Please enter a valid URL")]
        [Display(Name = "Logo URL")]
        public string? LogoUrl { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$", ErrorMessage = "Please enter a valid hex color (e.g., #007bff)")]
        [Display(Name = "Primary Color")]
        public string? PrimaryColor { get; set; }

        [MaxLength(50)]
        [RegularExpression(@"^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$", ErrorMessage = "Please enter a valid hex color (e.g., #6c757d)")]
        [Display(Name = "Accent Color")]
        public string? AccentColor { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        IsMultiTenantMode = _multiTenancyOptions.Enabled;

        // Get tenant - either from context (multi-tenant) or default (single-tenant)
        var tenantContext = _tenantAccessor.CurrentTenant;
        if (tenantContext == null)
        {
            _logger.LogWarning("No tenant context available");
            return RedirectToPage("/Admin/Index", new { tenantSlug = TenantSlug });
        }

        var tenantId = tenantContext.TenantId;

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            _logger.LogWarning("Tenant not found: {TenantId}", tenantId);
            return NotFound();
        }

        // Verify slug matches (in multi-tenant mode)
        if (IsMultiTenantMode && tenant.Slug != TenantSlug)
        {
            _logger.LogWarning("Tenant slug mismatch. Expected: {Expected}, Got: {Got}", tenant.Slug, TenantSlug);
            return RedirectToPage("/Admin/Branding", new { tenantSlug = tenant.Slug });
        }

        // Populate form
        Input = new BrandingInput
        {
            LogoUrl = tenant.LogoUrl,
            PrimaryColor = tenant.PrimaryColor,
            AccentColor = tenant.AccentColor
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        IsMultiTenantMode = _multiTenancyOptions.Enabled;

        if (!IsMultiTenantMode)
        {
            _logger.LogWarning("Branding update attempted in single-tenant mode");
            ModelState.AddModelError(string.Empty, "Branding customization is only available in multi-tenant mode.");
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Get tenant
        var tenantContext = _tenantAccessor.CurrentTenant;
        if (tenantContext == null)
        {
            _logger.LogWarning("No tenant context available for branding update");
            ModelState.AddModelError(string.Empty, "Unable to determine tenant context.");
            return Page();
        }

        var tenantId = tenantContext.TenantId;

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant == null)
        {
            _logger.LogWarning("Tenant not found for branding update: {TenantId}", tenantId);
            return NotFound();
        }

        // Update branding
        tenant.LogoUrl = string.IsNullOrWhiteSpace(Input.LogoUrl) ? null : Input.LogoUrl;
        tenant.PrimaryColor = string.IsNullOrWhiteSpace(Input.PrimaryColor) ? null : Input.PrimaryColor;
        tenant.AccentColor = string.IsNullOrWhiteSpace(Input.AccentColor) ? null : Input.AccentColor;

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Branding updated for tenant {TenantSlug} (ID: {TenantId}). Logo: {Logo}, Primary: {Primary}, Accent: {Accent}",
            tenant.Slug, tenant.Id, tenant.LogoUrl != null, tenant.PrimaryColor, tenant.AccentColor);

        SuccessMessage = "Branding settings saved successfully!";
        return Page();
    }
}
