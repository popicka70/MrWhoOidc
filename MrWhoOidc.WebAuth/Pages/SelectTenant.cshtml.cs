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

    private readonly ILogger<SelectTenantModel> _logger;
    private readonly ITenantSwitchingService _tenantSwitchingService;
    private readonly IGlobalAuthenticationService _globalAuthService;
    private readonly ITenantCredentialTicketStore _ticketStore;

    public SelectTenantModel(
        ILogger<SelectTenantModel> logger,
        ITenantSwitchingService tenantSwitchingService,
        IGlobalAuthenticationService globalAuthService,
        ITenantCredentialTicketStore ticketStore)
    {
        _logger = logger;
        _tenantSwitchingService = tenantSwitchingService;
        _globalAuthService = globalAuthService;
        _ticketStore = ticketStore;
    }

    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string? TicketId { get; set; }

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

        if (TryHydrateTicketFromTempData() && HasValidTicketForEmail())
        {
            RequiresVerification = false;
            if (!LoadAnonymousContext(includeTenants: true))
            {
                return RedirectToPage("/DiscoverTenant");
            }
        }
        else
        {
            RequiresVerification = true;
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
        TicketId = Request.Form[nameof(TicketId)];

        if (string.IsNullOrEmpty(Email))
        {
            _logger.LogWarning("Tenant selection submitted without email");
            return RedirectToPage("/DiscoverTenant");
        }

        var ticket = _ticketStore.GetTicket(TicketId ?? string.Empty);
        if (ticket is null || !string.Equals(ticket.EmailHash, HashEmail(Email), StringComparison.Ordinal))
        {
            _logger.LogWarning("Tenant selection attempted without verification for email hash {EmailHash}", HashEmail(Email));
            ErrorMessage = "Please confirm your password before selecting an organization.";
            RequiresVerification = true;
            TempData.Remove("TenantTicketId");
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
            var tenants = JsonSerializer.Deserialize<List<TenantInfo>>(tenantsJson);
            var selectedTenant = tenants?.FirstOrDefault(t => t.Slug == selectedTenantSlug);

            if (selectedTenant == null)
            {
                _logger.LogWarning("Invalid tenant slug selected: {TenantSlug}", selectedTenantSlug);
                ErrorMessage = "Invalid organization selected.";
                return await OnGetAsync();
            }

            if (!ticket.VerifiedUsers.Any(v => v.TenantId == selectedTenant.TenantId))
            {
                _logger.LogWarning("Tenant selection attempted for tenant {TenantSlug} without matching verification", selectedTenantSlug);
                ErrorMessage = "Please confirm your password again to access this organization.";
                RequiresVerification = true;
                TempData.Remove("TenantTicketId");
                return Page();
            }

            _logger.LogInformation(
                "User selected tenant: {TenantSlug} for email (hashed): {EmailHash}",
                selectedTenant.Slug,
                HashEmail(Email));

            // Build redirect URL with credential ticket to skip second password prompt
            var loginUrl = $"{selectedTenant.LoginUrl}?ticketId={TicketId}&email={Uri.EscapeDataString(Email)}";
            if (!string.IsNullOrEmpty(ReturnUrl))
            {
                loginUrl += $"&returnUrl={Uri.EscapeDataString(ReturnUrl)}";
            }

            // Clear session data
            HttpContext.Session.Remove(DiscoveredTenantsSessionKey);
            TempData.Remove("TenantTicketId");

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

        // Use global authentication service instead of per-tenant credential verifier
        var authResult = await _globalAuthService.AuthenticateAsync(Email!, Password, HttpContext.RequestAborted);

        if (!authResult.Succeeded)
        {
            _logger.LogWarning("Global authentication failed for email hash {EmailHash}: {Reason}", 
                HashEmail(Email!), authResult.FailureReason);
            ErrorMessage = "Invalid email or password.";
            RequiresVerification = true;
            Password = string.Empty;
            return Page();
        }

        // Convert memberships to VerifiedTenantUser format for ticket store
        // The ticket stores TenantId access verification; UserId is a placeholder (not used)
        // Login page looks up the actual User by email in the target tenant
        var verifiedUsers = authResult.Memberships
            .Select(m => new VerifiedTenantUser(m.TenantId, m.UserAccountId))
            .ToList();

        var ticket = _ticketStore.CreateTicket(Email!, verifiedUsers);
        TicketId = ticket.TicketId;
        TempData["TenantTicketId"] = TicketId;
        TempData.Keep("TenantTicketId");
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

        TicketId ??= TempData["TenantTicketId"] as string;
        if (!string.IsNullOrEmpty(TicketId))
        {
            TempData["TenantTicketId"] = TicketId;
            TempData.Keep("TenantTicketId");
        }

        TempData.Keep("Email");
        TempData.Keep("ReturnUrl");
        return true;
    }

    private bool TryHydrateTicketFromTempData()
    {
        if (!string.IsNullOrEmpty(TicketId))
        {
            return true;
        }

        var tempTicketId = TempData["TenantTicketId"] as string;
        if (string.IsNullOrEmpty(tempTicketId))
        {
            return false;
        }

        TicketId = tempTicketId;
        TempData.Keep("TenantTicketId");
        return true;
    }

    private bool HasValidTicketForEmail()
    {
        if (string.IsNullOrEmpty(TicketId) || string.IsNullOrEmpty(Email))
        {
            return false;
        }

        var ticket = _ticketStore.GetTicket(TicketId);
        if (ticket is null)
        {
            return false;
        }

        if (!string.Equals(ticket.EmailHash, HashEmail(Email), StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
