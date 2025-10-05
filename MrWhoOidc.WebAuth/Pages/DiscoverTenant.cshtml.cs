using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Email-first tenant discovery page.
/// Users enter their email to find which tenant(s) they have access to.
/// </summary>
[EnableRateLimiting("email-discovery")]
public class DiscoverTenantModel : PageModel
{
    private readonly ITenantDiscoveryService _tenantDiscovery;
    private readonly ILogger<DiscoverTenantModel> _logger;

    public DiscoverTenantModel(
        ITenantDiscoveryService tenantDiscovery,
        ILogger<DiscoverTenantModel> logger)
    {
        _tenantDiscovery = tenantDiscovery;
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

    public void OnGet()
    {
        _logger.LogDebug("Tenant discovery page accessed, ReturnUrl: {ReturnUrl}", ReturnUrl ?? "(none)");
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

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
    }
}
