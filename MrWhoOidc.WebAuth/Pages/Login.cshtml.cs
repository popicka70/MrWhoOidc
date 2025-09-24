using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Pages;

public class LoginModel(IUserService users, ILogger<LoginModel> logger, ITotpService totp, MrWhoOidc.Auth.Persistence.AuthDbContext db) : PageModel
{
    private static readonly Dictionary<string, (int Attempts, DateTimeOffset First)> _attempts = new();
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
        // Intentionally left blank
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

        var user = await users.FindByUsernameAsync(Username);
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
        logger.LogInformation("User {User} signed in", Username);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
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
