using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages;

[AllowAnonymous]
public class LoginTotpModel(AuthDbContext db, ITotpService totp, ILogger<LoginTotpModel> logger) : PageModel
{
    [BindProperty]
    [Required, StringLength(6, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var preauth = await HttpContext.AuthenticateAsync("preauth");
        if (!preauth.Succeeded)
            return RedirectToPage("/Login", new { ReturnUrl });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var preauth = await HttpContext.AuthenticateAsync("preauth");
        if (!preauth.Succeeded)
            return RedirectToPage("/Login", new { ReturnUrl });

        var sub = preauth.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return RedirectToPage("/Login", new { ReturnUrl });

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
            return RedirectToPage("/Login", new { ReturnUrl });

        if (!totp.VerifyCode(user.TotpSecret, Code, digits: 6, period: 30, window: 1))
        {
            ModelState.AddModelError(string.Empty, "Invalid code");
            return Page();
        }

        await HttpContext.SignOutAsync("preauth");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new("amr", "mfa"),
            new("idp", "local")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        logger.LogInformation("User {User} finished MFA", user.Username);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
    }
}
