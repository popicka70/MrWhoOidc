using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Tenant selection page for users who have accounts in multiple tenants.
/// Displays tenant cards and allows user to choose which organization to sign in to.
/// </summary>
public class SelectTenantModel : PageModel
{
    private readonly ILogger<SelectTenantModel> _logger;
    private readonly ITenantSwitchingService _tenantSwitchingService;

    public SelectTenantModel(ILogger<SelectTenantModel> logger, ITenantSwitchingService tenantSwitchingService)
    {
        _logger = logger;
        _tenantSwitchingService = tenantSwitchingService;
    }

    public string? Email { get; set; }
    public string? ReturnUrl { get; set; }
    public List<TenantInfo>? Tenants { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // If user is authenticated, load tenants from service
        if (User.Identity?.IsAuthenticated == true)
        {
            Email = User.FindFirst(ClaimTypes.Email)?.Value ?? User.Identity.Name;
            var userTenants = await _tenantSwitchingService.GetUserTenantsAsync(User);
            
            Tenants = userTenants.Select(t => new TenantInfo
            {
                Name = t.TenantName,
                Slug = t.TenantSlug,
                LoginUrl = $"/t/{t.TenantSlug}/account", // Direct to dashboard
                LogoUrl = null, // TODO: Add logo support in TenantAccessInfo
                LastLoginAt = null
            }).ToList();

            return Page();
        }

        // Retrieve data from TempData (set by DiscoverTenant page)
        Email = TempData["Email"] as string;
        ReturnUrl = TempData["ReturnUrl"] as string;

        if (string.IsNullOrEmpty(Email))
        {
            _logger.LogWarning("SelectTenant page accessed without email in TempData");
            return RedirectToPage("/DiscoverTenant");
        }

        // Retrieve tenant list from session
        var tenantsJson = HttpContext.Session.GetString("DiscoveredTenants");
        if (string.IsNullOrEmpty(tenantsJson))
        {
            _logger.LogWarning("SelectTenant page accessed without tenant list in session");
            ErrorMessage = "Session expired. Please enter your email again.";
            return RedirectToPage("/DiscoverTenant");
        }

        try
        {
            Tenants = System.Text.Json.JsonSerializer.Deserialize<List<TenantInfo>>(tenantsJson);

            if (Tenants == null || !Tenants.Any())
            {
                _logger.LogWarning("Deserialized tenant list is empty");
                return RedirectToPage("/DiscoverTenant");
            }

            _logger.LogDebug("Displaying {Count} tenants for selection", Tenants.Count);

            // Preserve TempData for potential reloads
            TempData.Keep("Email");
            TempData.Keep("ReturnUrl");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deserializing tenant list from session");
            ErrorMessage = "An error occurred loading your organizations. Please try again.";
            return RedirectToPage("/DiscoverTenant");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string selectedTenantSlug)
    {
        // If authenticated, handle switch via service
        if (User.Identity?.IsAuthenticated == true)
        {
            if (string.IsNullOrEmpty(selectedTenantSlug))
            {
                ErrorMessage = "Please select an organization.";
                return await OnGetAsync();
            }

            var userTenants = await _tenantSwitchingService.GetUserTenantsAsync(User);
            var targetTenant = userTenants.FirstOrDefault(t => t.TenantSlug == selectedTenantSlug);

            if (targetTenant == null)
            {
                ErrorMessage = "You do not have access to this organization.";
                return await OnGetAsync();
            }

            await _tenantSwitchingService.SwitchTenantAsync(HttpContext, targetTenant.TenantId);
            return Redirect($"/t/{targetTenant.TenantSlug}/account");
        }

        Email = Request.Form["Email"];
        ReturnUrl = Request.Form["ReturnUrl"];

        if (string.IsNullOrEmpty(selectedTenantSlug))
        {
            _logger.LogWarning("Tenant selection submitted without selectedTenantSlug");
            ErrorMessage = "Please select an organization.";
            return await OnGetAsync();
        }

        if (string.IsNullOrEmpty(Email))
        {
            _logger.LogWarning("Tenant selection submitted without email");
            return RedirectToPage("/DiscoverTenant");
        }

        // Retrieve tenant list to validate selection
        var tenantsJson = HttpContext.Session.GetString("DiscoveredTenants");
        if (string.IsNullOrEmpty(tenantsJson))
        {
            _logger.LogWarning("Session expired during tenant selection");
            return RedirectToPage("/DiscoverTenant");
        }

        try
        {
            var tenants = System.Text.Json.JsonSerializer.Deserialize<List<TenantInfo>>(tenantsJson);
            var selectedTenant = tenants?.FirstOrDefault(t => t.Slug == selectedTenantSlug);

            if (selectedTenant == null)
            {
                _logger.LogWarning("Invalid tenant slug selected: {TenantSlug}", selectedTenantSlug);
                ErrorMessage = "Invalid organization selected.";
                return await OnGetAsync();
            }

            _logger.LogInformation(
                "User selected tenant: {TenantSlug} for email (hashed): {EmailHash}",
                selectedTenant.Slug,
                HashEmail(Email));

            // Build redirect URL with email pre-filled
            var loginUrl = $"{selectedTenant.LoginUrl}?email={Uri.EscapeDataString(Email)}";
            if (!string.IsNullOrEmpty(ReturnUrl))
            {
                loginUrl += $"&returnUrl={Uri.EscapeDataString(ReturnUrl)}";
            }

            // Clear session data
            HttpContext.Session.Remove("DiscoveredTenants");

            return Redirect(loginUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing tenant selection");
            ErrorMessage = "An error occurred. Please try again.";
            return await OnGetAsync();
        }
    }

    private static string HashEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "empty";

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
    }
}
