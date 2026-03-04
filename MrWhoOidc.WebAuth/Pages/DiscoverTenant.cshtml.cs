using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Represents an external identity provider option available for login.
/// </summary>
public sealed record LoginIdpOption
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? LogoUrl { get; init; }
    public bool HasLogoData { get; init; }
    public string? ButtonBackgroundColor { get; init; }
    public string? ButtonTextColor { get; init; }
}

/// <summary>
/// Email-first tenant discovery page.
/// Users enter their email to find which tenant(s) they have access to.
/// Also shows external IdP login options for IdPs configured with AllowRegistration=true in the default tenant.
/// </summary>
[EnableRateLimiting("email-discovery")]
public class DiscoverTenantModel : PageModel
{
    private readonly ITenantDiscoveryService _tenantDiscovery;
    private readonly AuthDbContext _dbContext;
    private readonly IMultiTenancyOptions _multiTenancyOptions;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly IOptions<QrLoginOptions> _qrOptions;
    private readonly ILogger<DiscoverTenantModel> _logger;

    public DiscoverTenantModel(
        ITenantDiscoveryService tenantDiscovery,
        AuthDbContext dbContext,
        IMultiTenancyOptions multiTenancyOptions,
        IPlatformSettingsService platformSettings,
        IOptions<QrLoginOptions> qrOptions,
        ILogger<DiscoverTenantModel> logger)
    {
        _tenantDiscovery = tenantDiscovery;
        _dbContext = dbContext;
        _multiTenancyOptions = multiTenancyOptions;
        _platformSettings = platformSettings;
        _qrOptions = qrOptions;
        _logger = logger;
    }

    [BindProperty]
    [Required(ErrorMessage = "Email address is required")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address")]
    public string Email { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public bool ShowDirectLoginLink { get; set; } = true;

    /// <summary>
    /// External identity providers available for login (from default tenant with AllowRegistration=true).
    /// </summary>
    public List<LoginIdpOption> LoginIdps { get; private set; } = [];

    /// <summary>
    /// Whether to show the QR login option (based on platform settings and QR login being enabled).
    /// </summary>
    public bool ShowQrLogin { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        _logger.LogDebug("Tenant discovery page accessed, ReturnUrl: {ReturnUrl}", ReturnUrl ?? "(none)");

        // If user is already authenticated (e.g., returned from external IdP login), redirect them
        if (User.Identity?.IsAuthenticated == true)
        {
            _logger.LogInformation("User already authenticated, redirecting from DiscoverTenant");

            // If there's a return URL, go there (it's likely an OIDC authorize flow)
            if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            {
                return LocalRedirect(ReturnUrl);
            }

            // Otherwise, redirect to home page
            return Redirect("/");
        }

        await LoadLoginIdpsAsync();
        
        // Check if QR login should be shown
        var qrGloballyEnabled = _qrOptions.Value.Enabled;
        var qrPlatformEnabled = await _platformSettings.IsQrLoginAtDiscoveryEnabledAsync();
        ShowQrLogin = qrGloballyEnabled && qrPlatformEnabled;
        
        _logger.LogDebug("QR login visibility: globalEnabled={GlobalEnabled}, platformEnabled={PlatformEnabled}, show={Show}",
            qrGloballyEnabled, qrPlatformEnabled, ShowQrLogin);

        return Page();
    }

    private async Task LoadLoginIdpsAsync()
    {
        try
        {
            // Get the default tenant slug from configuration
            var defaultTenantSlug = _multiTenancyOptions.DefaultTenantSlug ?? "default";

            // Get the default tenant ID
            var defaultTenantId = await _dbContext.Tenants
                .AsNoTracking()
                .Where(t => t.Slug == defaultTenantSlug && t.Status == TenantStatus.Active)
                .Select(t => t.Id)
                .FirstOrDefaultAsync();

            if (defaultTenantId == Guid.Empty)
            {
                _logger.LogWarning("No default tenant found for login IdP loading (slug: {Slug})", defaultTenantSlug);
                return;
            }

            // Load IdPs that are enabled and allow registration (same rules as registration page)
            LoginIdps = await _dbContext.IdentityProviders
                .AsNoTracking()
                .Where(p => p.TenantId == defaultTenantId
                         && p.Enabled
                         && p.AllowRegistration)
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.DisplayName)
                .Select(p => new LoginIdpOption
                {
                    Id = p.Id,
                    Name = p.Name,
                    DisplayName = p.DisplayName ?? p.Name,
                    UpdatedAt = p.UpdatedAt,
                    LogoUrl = p.LogoUrl,
                    HasLogoData = p.LogoData != null,
                    ButtonBackgroundColor = p.ButtonBackgroundColor,
                    ButtonTextColor = p.ButtonTextColor
                })
                .ToListAsync();

            _logger.LogDebug("Loaded {Count} login IdPs for tenant discovery page", LoginIdps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load login IdPs");
            // Don't throw - page should still render with email-first flow
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            // Find all tenants where this email has an account
            var tenants = await _tenantDiscovery.FindTenantsByEmailAsync(Email);

            _logger.LogInformation(
                "Tenant discovery for email (hashed): {EmailHash}, found {Count} tenant(s), IP: {IP}",
                HashEmail(Email),
                tenants.Count,
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            // Handle results based on number of tenants found
            switch (tenants.Count)
            {
                case 0:
                    // No tenants found - show error
                    ErrorMessage = "No account found with this email address. Please check your email or register for a new account.";
                    _logger.LogDebug("No tenants found for email (hashed): {EmailHash}", HashEmail(Email));
                    return Page();

                case 1:
                    // Exactly one tenant - auto-redirect to that tenant's login
                    var tenant = tenants[0];
                    _logger.LogInformation(
                        "Auto-redirecting to single tenant: {TenantSlug} for email (hashed): {EmailHash}",
                        tenant.Slug,
                        HashEmail(Email));

                    // Store email in TempData for pre-fill on login page
                    TempData["PrefilledEmail"] = Email;

                    // Build redirect URL with email pre-filled
                    var loginUrl = $"{tenant.LoginUrl}?email={Uri.EscapeDataString(Email)}";
                    if (!string.IsNullOrEmpty(ReturnUrl))
                    {
                        loginUrl += $"&returnUrl={Uri.EscapeDataString(ReturnUrl)}";
                    }

                    return Redirect(loginUrl);

                default:
                    // Multiple tenants - redirect to selection page
                    _logger.LogInformation(
                        "Multiple tenants ({Count}) found for email (hashed): {EmailHash}, redirecting to selection",
                        tenants.Count,
                        HashEmail(Email));

                    // Store email and tenant list in TempData for selection page
                    TempData["Email"] = Email;
                    TempData["ReturnUrl"] = ReturnUrl;
                    TempData["TenantCount"] = tenants.Count;

                    // Store tenant info in session (serialize to JSON)
                    HttpContext.Session.SetString("DiscoveredTenants",
                        System.Text.Json.JsonSerializer.Serialize(tenants));

                    return RedirectToPage("/SelectTenant");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during tenant discovery for email (hashed): {EmailHash}", HashEmail(Email));
            ErrorMessage = "An error occurred while looking up your account. Please try again.";
            return Page();
        }
    }

    /// <summary>
    /// Hash email for privacy in logs
    /// </summary>
    private static string HashEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "empty";

        return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(email.ToLowerInvariant())[..8];
    }
}

