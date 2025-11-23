using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.WebAuth.Pages;

public class LoginModel(
    IUserService users,
    ILogger<LoginModel> logger,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantSettingsService settingsService,
    ITenantBrandingService brandingService,
    ITenantCredentialTicketStore ticketStore) : PageModel
{
    private static readonly Dictionary<string, (int Attempts, DateTimeOffset First)> _attempts = new();
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [BindProperty]
    // Accepts either traditional username or an email address.
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Email { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TicketId { get; set; }

    public bool ShowNotYouLink => !string.IsNullOrEmpty(Email);

    public TenantBranding? TenantBranding { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        logger.LogInformation("🔍 [Login Page GET] ReturnUrl: {ReturnUrl}, Email: {Email}",
            ReturnUrl ?? "(null)",
            Email ?? "(null)");

        // Pre-fill username with email if provided
        if (!string.IsNullOrEmpty(Email))
        {
            Username = Email;
        }

        // Load tenant branding for display
        try
        {
            TenantBranding = await brandingService.GetCurrentTenantBrandingAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load tenant branding, using default");
            TenantBranding = null;
        }

        if (!string.IsNullOrEmpty(TicketId))
        {
            var result = await TryCompleteLoginWithTicketAsync();
            if (result is not null)
            {
                return result;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        logger.LogInformation("🔐 [Login POST] Username={Username}, ReturnUrl={ReturnUrl}, ModelStateValid={Valid}, CurrentTenant={TenantSlug}, TenantId={TenantId}",
            Username ?? "(null)", 
            ReturnUrl ?? "(null)", 
            ModelState.IsValid,
            tenantAccessor.CurrentTenant?.Slug ?? "(null)",
            tenantAccessor.CurrentTenant?.TenantId.ToString() ?? "(null)");

        if (!ModelState.IsValid)
            return Page();

        // Validate username is not null
        if (string.IsNullOrWhiteSpace(Username))
        {
            ModelState.AddModelError(string.Empty, "Username is required");
            return Page();
        }

        if (IsLockedOut(HttpContext, Username))
        {
            ModelState.AddModelError(string.Empty, "Too many attempts. Try again later.");
            return Page();
        }

        // Try username first; fallback to email/alternative email match.
        var user = await users.FindByUsernameAsync(Username) ?? await users.FindByUsernameOrEmailAsync(Username);
        logger.LogInformation("🔍 [Login POST] User lookup result: {UserFound}, Username={Username}",
            user != null ? "FOUND" : "NOT FOUND", Username);
        
        if (user is null || !await users.VerifyPasswordAsync(user, Password))
        {
            logger.LogWarning("⚠️ [Login POST] Authentication failed: user={UserNull}, passwordValid={PasswordCheck}",
                user == null ? "NULL" : "EXISTS",
                user == null ? "N/A" : (await users.VerifyPasswordAsync(user, Password) ? "VALID" : "INVALID"));
            RegisterFailedAttempt(HttpContext, Username);
            ModelState.AddModelError(string.Empty, "Invalid username or password");
            return Page();
        }

        return await CompleteSignInAsync(user);
    }

    static string Key(HttpContext ctx, string username) => $"{ctx.Connection.RemoteIpAddress}-{username}";

    static bool IsLockedOut(HttpContext ctx, string username)
    {
        var key = Key(ctx, username);
        if (_attempts.TryGetValue(key, out var info))
        {
            if (DateTimeOffset.UtcNow - info.First > Window)
            {
                _attempts.Remove(key);
                return false;
            }
            return info.Attempts >= MaxAttempts;
        }
        return false;
    }

    static void RegisterFailedAttempt(HttpContext ctx, string username)
    {
        var key = Key(ctx, username);
        if (_attempts.TryGetValue(key, out var info))
        {
            if (DateTimeOffset.UtcNow - info.First > Window)
                _attempts[key] = (1, DateTimeOffset.UtcNow);
            else
                _attempts[key] = (info.Attempts + 1, info.First);
        }
        else
        {
            _attempts[key] = (1, DateTimeOffset.UtcNow);
        }
    }

    static void ClearAttempts(HttpContext ctx, string username)
    {
        var key = Key(ctx, username);
        _attempts.Remove(key);
    }

    private async Task<IActionResult?> TryCompleteLoginWithTicketAsync()
    {
        if (string.IsNullOrEmpty(TicketId))
        {
            return null;
        }

        var ticket = ticketStore.GetTicket(TicketId);
        if (ticket is null)
        {
            logger.LogInformation("Tenant credential ticket {TicketId} missing or expired", TicketId);
            ModelState.AddModelError(string.Empty, "Your verification expired. Please enter your password again.");
            TicketId = null;
            return null;
        }

        if (string.IsNullOrEmpty(Email) || !string.Equals(ticket.EmailHash, HashEmail(Email), StringComparison.Ordinal))
        {
            logger.LogWarning("Ticket {TicketId} email hash mismatch", TicketId);
            ModelState.AddModelError(string.Empty, "We could not confirm your email for this session. Please sign in again.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        var tenant = tenantAccessor.CurrentTenant;
        if (tenant is null)
        {
            logger.LogWarning("Ticket {TicketId} used without active tenant context", TicketId);
            ModelState.AddModelError(string.Empty, "We could not determine your organization. Please start over.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        var verifiedUser = ticket.VerifiedUsers.FirstOrDefault(v => v.TenantId == tenant.TenantId);
        if (verifiedUser == null)
        {
            logger.LogWarning("Ticket {TicketId} does not include tenant {TenantId}", TicketId, tenant.TenantId);
            ModelState.AddModelError(string.Empty, "Please confirm your password again to access this organization.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        var user = await users.FindByIdAcrossTenantsAsync(verifiedUser.UserId);
        if (user is null)
        {
            logger.LogWarning("Ticket {TicketId} referenced missing user {UserId}", TicketId, verifiedUser.UserId);
            ModelState.AddModelError(string.Empty, "Your account could not be found. Please sign in again.");
            TicketId = null;
            ticketStore.RemoveTicket(ticket.TicketId);
            return null;
        }

        Username = user.Username;
        var result = await CompleteSignInAsync(user);
        ticketStore.RemoveTicket(ticket.TicketId);
        TempData.Remove("TenantTicketId");
        return result;
    }

    private async Task<IActionResult> CompleteSignInAsync(User user)
    {
        // Check tenant MFA requirement
        var settings = await settingsService.GetCurrentTenantSettingsAsync();
        var mfaRequired = settings.Auth?.RequireMfa ?? false;

        // If MFA is required but user doesn't have it enabled, redirect to enrollment
        if (mfaRequired && !user.TotpEnabled)
        {
            var preauthClaims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(OidcConstants.Claims.Amr, "pwd"),
                new("mfa_enrollment_required", "true")
            };
            var preauthIdentity = new ClaimsIdentity(preauthClaims, "preauth");
            await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(preauthIdentity));
            ClearAttempts(HttpContext, user.Username);

            logger.LogInformation("⚠️ [Login] User {User} requires MFA enrollment (tenant policy). Redirecting to /Mfa", user.Username);
            var enrollUrl = Url.Page("/Mfa/Index", null, new { required = true, returnUrl = ReturnUrl }, protocol: Request.Scheme);
            return Redirect(enrollUrl ?? "/Mfa/Index?required=true");
        }

        // If TOTP enabled, issue short-lived preauth and redirect to TOTP page
        if (user.TotpEnabled)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(OidcConstants.Claims.Amr, "pwd")
            };
            var identity = new ClaimsIdentity(claims, "preauth");
            await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(identity));
            ClearAttempts(HttpContext, user.Username);
            var url = Url.Page("/LoginTotp", null, new { ReturnUrl }, protocol: Request.Scheme);
            return Redirect(url ?? "/LoginTotp");
        }

        var finalClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new(OidcConstants.Claims.Amr, "pwd"),
            new(OidcConstants.Claims.Idp, "local")
        };

        var finalIdentity = new ClaimsIdentity(finalClaims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(finalIdentity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        ClearAttempts(HttpContext, user.Username);
        logger.LogInformation("✅ [Login] User {User} signed in successfully", user.Username);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            logger.LogInformation("➡️ [Login] Redirecting to ReturnUrl: {ReturnUrl}", ReturnUrl);
            return LocalRedirect(ReturnUrl);
        }

        // Build tenant-aware default redirect URL based on mode
        var currentTenant = tenantAccessor.CurrentTenant;
        string defaultUrl;

        if (multiTenancyOptions.Enabled && currentTenant != null)
        {
            defaultUrl = $"/t/{currentTenant.Slug}/";
            logger.LogInformation("➡️ [Login] Multi-tenant mode: redirecting to {DefaultUrl} (Tenant: {TenantSlug})",
                defaultUrl, currentTenant.Slug);
        }
        else
        {
            defaultUrl = "/";
            logger.LogInformation("➡️ [Login] Single-tenant mode: redirecting to {DefaultUrl}", defaultUrl);
        }

        return LocalRedirect(defaultUrl);
    }

    private static string HashEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return "empty";
        }

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
        return Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
    }
}
