using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages;

[AllowAnonymous]
public class LoginTotpModel(
    AuthDbContext db, 
    ITotpService totp, 
    IUserAccountService userAccountService,
    ILogger<LoginTotpModel> logger) : PageModel
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

        // Get the per-tenant user to look up the linked UserAccount
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || string.IsNullOrEmpty(user.Email))
            return RedirectToPage("/Login", new { ReturnUrl });

        // Get MFA settings from UserAccount (global)
        var account = await userAccountService.FindByEmailAsync(user.Email);
        if (account is null)
            return RedirectToPage("/Login", new { ReturnUrl });

        var (mfaEnabled, totpSecret) = await userAccountService.GetMfaStatusAsync(account.Id);
        if (!mfaEnabled || string.IsNullOrEmpty(totpSecret))
            return RedirectToPage("/Login", new { ReturnUrl });

        if (!totp.VerifyCode(totpSecret, Code, digits: 6, period: 30, window: 1))
        {
            ModelState.AddModelError(string.Empty, "Invalid code");
            return Page();
        }

        await HttpContext.SignOutAsync("preauth");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new(OidcConstants.Claims.Amr, "mfa"),
            new(OidcConstants.Claims.Idp, "local")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        logger.LogInformation("User {User} finished MFA (validated against global UserAccount)", user.Username);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
    }
}
