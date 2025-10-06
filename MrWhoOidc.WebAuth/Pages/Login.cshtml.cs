using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Pages;

public class LoginModel(
    IUserService users, 
    ILogger<LoginModel> logger,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
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

    public bool ShowNotYouLink => !string.IsNullOrEmpty(Email);

    public void OnGet()
    {
        logger.LogInformation("🔍 [Login Page GET] ReturnUrl: {ReturnUrl}, Email: {Email}", 
            ReturnUrl ?? "(null)", 
            Email ?? "(null)");

        // Pre-fill username with email if provided
        if (!string.IsNullOrEmpty(Email))
        {
            Username = Email;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        if (IsLockedOut(HttpContext, Username))
        {
            ModelState.AddModelError(string.Empty, "Too many attempts. Try again later.");
            return Page();
        }

        // Try username first; fallback to email/alternative email match.
        var user = await users.FindByUsernameAsync(Username) ?? await users.FindByUsernameOrEmailAsync(Username);
        if (user is null || !await users.VerifyPasswordAsync(user, Password))
        {
            RegisterFailedAttempt(HttpContext, Username);
            ModelState.AddModelError(string.Empty, "Invalid username or password");
            return Page();
        }

        // If TOTP enabled, issue short-lived preauth and redirect to TOTP page
        if (user.TotpEnabled)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new("amr", "pwd")
            };
            var identity = new ClaimsIdentity(claims, "preauth");
            await HttpContext.SignInAsync("preauth", new ClaimsPrincipal(identity));
            ClearAttempts(HttpContext, Username);
            var url = Url.Page("/LoginTotp", null, new { ReturnUrl }, protocol: Request.Scheme);
            return Redirect(url ?? "/LoginTotp");
        }

        var finalClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new("amr", "pwd"),
            new("idp", "local")
        };

        var finalIdentity = new ClaimsIdentity(finalClaims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(finalIdentity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        ClearAttempts(HttpContext, Username);
        logger.LogInformation("✅ [Login] User {User} signed in successfully", Username);

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
            // Multi-tenant mode: redirect to /t/{slug}/
            defaultUrl = $"/t/{currentTenant.Slug}/";
            logger.LogInformation("➡️ [Login] Multi-tenant mode: redirecting to {DefaultUrl} (Tenant: {TenantSlug})", 
                defaultUrl, currentTenant.Slug);
        }
        else
        {
            // Single-tenant mode: redirect to root /
            defaultUrl = "/";
            logger.LogInformation("➡️ [Login] Single-tenant mode: redirecting to {DefaultUrl}", defaultUrl);
        }
        
        return LocalRedirect(defaultUrl);
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
}
