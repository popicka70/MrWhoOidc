using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Email-first tenant discovery page.
/// Users enter their email to find which tenant(s) they have access to.
/// </summary>
[EnableRateLimiting("email-discovery")]
public class DiscoverTenantModel : PageModel
{
    private readonly AuthDbContext _db;
    private readonly ITenantDiscoveryService _tenantDiscovery;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly IOptions<QrLoginOptions> _qrOptions;
    private readonly ILogger<DiscoverTenantModel> _logger;

    public DiscoverTenantModel(
        AuthDbContext db,
        ITenantDiscoveryService tenantDiscovery,
        IPlatformSettingsService platformSettings,
        IOptions<QrLoginOptions> qrOptions,
        ILogger<DiscoverTenantModel> logger)
    {
        _db = db;
        _tenantDiscovery = tenantDiscovery;
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

    public bool TenantDiscoveryEnabled { get; private set; } = true;

    /// <summary>
    /// Whether to show the QR login option (based on platform settings and QR login being enabled).
    /// </summary>
    public bool ShowQrLogin { get; set; }

    public IReadOnlyList<IdentityProvider> PlatformProviders { get; private set; } = Array.Empty<IdentityProvider>();

    public string PlatformProviderReturnUrl =>
        !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && ReturnUrl.StartsWith("/platform-admin", StringComparison.OrdinalIgnoreCase)
            ? ReturnUrl
            : "/platform-admin";

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

        await LoadPagePolicyAsync();

        return Page();
    }

    private async Task LoadPagePolicyAsync()
    {
        var settings = await _platformSettings.GetSettingsAsync();
        TenantDiscoveryEnabled = settings.RootLoginMode == RootLoginMode.TenantDiscovery;

        PlatformProviders = await _db.IdentityProviders.AsNoTracking()
            .Where(p => p.TenantId == null && p.Enabled)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName ?? p.Name)
            .ToListAsync();

        var qrGloballyEnabled = _qrOptions.Value.Enabled;
        var qrPlatformEnabled = settings.QrLoginAtDiscoveryEnabled;
        ShowQrLogin = TenantDiscoveryEnabled && qrGloballyEnabled && qrPlatformEnabled;

        _logger.LogDebug(
            "Root login policy: mode={RootLoginMode}, tenantDiscovery={TenantDiscoveryEnabled}, qrGlobal={GlobalEnabled}, qrPlatform={PlatformEnabled}, showQr={ShowQr}",
            settings.RootLoginMode,
            TenantDiscoveryEnabled,
            qrGloballyEnabled,
            qrPlatformEnabled,
            ShowQrLogin);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadPagePolicyAsync();

        if (!TenantDiscoveryEnabled)
        {
            ErrorMessage = "Use your organization-specific sign-in URL to continue.";
            return Page();
        }

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

