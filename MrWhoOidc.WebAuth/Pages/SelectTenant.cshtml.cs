using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages;

/// <summary>
/// Tenant selection page for users who have accounts in multiple tenants.
/// Displays tenant cards and allows user to choose which organization to sign in to.
/// </summary>
public class SelectTenantModel : PageModel
{
    private const string DiscoveredTenantsSessionKey = "DiscoveredTenants";
    private const string VerificationTicketSessionKey = "TenantDiscoveryVerified";
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(5);

    private readonly ILogger<SelectTenantModel> _logger;
    private readonly ITenantSwitchingService _tenantSwitchingService;
    private readonly ITenantCredentialVerifier _credentialVerifier;

    public SelectTenantModel(
        ILogger<SelectTenantModel> logger,
        ITenantSwitchingService tenantSwitchingService,
        ITenantCredentialVerifier credentialVerifier)
    {
        _logger = logger;
        _tenantSwitchingService = tenantSwitchingService;
        _credentialVerifier = credentialVerifier;
    }

    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public List<TenantInfo>? Tenants { get; set; }
    public string? ErrorMessage { get; set; }
    public bool RequiresVerification { get; set; } = true;

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
                LoginUrl = $"/t/{t.TenantSlug}/account",
                LogoUrl = null,
                LastLoginAt = null
            }).ToList();

            RequiresVerification = false;
            return Page();
        }

        if (!LoadAnonymousContext(includeTenants: false))
        {
            return RedirectToPage("/DiscoverTenant");
        }

        RequiresVerification = !HasValidVerificationTicket(Email!);

        if (!RequiresVerification)
        {
            if (!LoadAnonymousContext(includeTenants: true))
            {
                return RedirectToPage("/DiscoverTenant");
            }
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

        if (string.IsNullOrEmpty(Email))
        {
            _logger.LogWarning("Tenant selection submitted without email");
            return RedirectToPage("/DiscoverTenant");
        }

        if (!HasValidVerificationTicket(Email))
        {
            _logger.LogWarning("Tenant selection attempted without verification for email hash {EmailHash}", HashEmail(Email));
            ErrorMessage = "Please confirm your password before selecting an organization.";
            RequiresVerification = true;
            LoadAnonymousContext(includeTenants: false);
            return Page();
        }

        if (string.IsNullOrEmpty(selectedTenantSlug))
        {
            _logger.LogWarning("Tenant selection submitted without selectedTenantSlug");
            ErrorMessage = "Please select an organization.";
            return await OnGetAsync();
        }

        // Retrieve tenant list to validate selection
        var tenantsJson = HttpContext.Session.GetString(DiscoveredTenantsSessionKey);
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
            HttpContext.Session.Remove(DiscoveredTenantsSessionKey);
            HttpContext.Session.Remove(VerificationTicketSessionKey);

            return Redirect(loginUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing tenant selection");
            ErrorMessage = "An error occurred. Please try again.";
            return await OnGetAsync();
        }
    }

    public async Task<IActionResult> OnPostVerifyAsync()
    {
        Email = Request.Form["Email"];
        ReturnUrl = Request.Form["ReturnUrl"];

        if (!LoadAnonymousContext(includeTenants: false))
        {
            return RedirectToPage("/DiscoverTenant");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Password is required.";
            RequiresVerification = true;
            return Page();
        }

        var verificationResult = await _credentialVerifier.VerifyAsync(Email!, Password, HttpContext.RequestAborted);

        if (!verificationResult.Success)
        {
            ErrorMessage = "Invalid email or password.";
            RequiresVerification = true;
            Password = string.Empty;
            return Page();
        }

        StoreVerificationTicket(Email!);
        Password = string.Empty;
        return RedirectToPage(new { ReturnUrl });
    }

    private static string HashEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "empty";

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
    }

    private bool LoadAnonymousContext(bool includeTenants)
    {
        Email ??= TempData["Email"] as string;
        ReturnUrl ??= TempData["ReturnUrl"] as string;

        if (string.IsNullOrEmpty(Email))
        {
            _logger.LogWarning("SelectTenant page accessed without email in TempData");
            return false;
        }

        var tenantsJson = HttpContext.Session.GetString(DiscoveredTenantsSessionKey);
        if (string.IsNullOrEmpty(tenantsJson))
        {
            _logger.LogWarning("SelectTenant page accessed without tenant list in session");
            ErrorMessage = "Session expired. Please enter your email again.";
            return false;
        }

        if (includeTenants)
        {
            try
            {
                Tenants = JsonSerializer.Deserialize<List<TenantInfo>>(tenantsJson);

                if (Tenants == null || !Tenants.Any())
                {
                    _logger.LogWarning("Deserialized tenant list is empty");
                    return false;
                }

                    _logger.LogDebug("Displaying {Count} tenants for selection", Tenants.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deserializing tenant list from session");
                ErrorMessage = "An error occurred loading your organizations. Please try again.";
                return false;
            }
        }

        TempData.Keep("Email");
        TempData.Keep("ReturnUrl");
        return true;
    }

    private bool HasValidVerificationTicket(string email)
    {
        var json = HttpContext.Session.GetString(VerificationTicketSessionKey);
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            var ticket = JsonSerializer.Deserialize<TenantVerificationTicket>(json);
            if (ticket == null)
            {
                return false;
            }

            if (!string.Equals(ticket.EmailHash, HashEmail(email), StringComparison.Ordinal))
            {
                return false;
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(ticket.IssuedAtUnixSeconds);
            if (DateTimeOffset.UtcNow - issuedAt > VerificationLifetime)
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void StoreVerificationTicket(string email)
    {
        var ticket = new TenantVerificationTicket(HashEmail(email), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        HttpContext.Session.SetString(VerificationTicketSessionKey, JsonSerializer.Serialize(ticket));
    }

    private sealed record TenantVerificationTicket(string EmailHash, long IssuedAtUnixSeconds);
}
