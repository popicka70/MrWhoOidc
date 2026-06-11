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
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages;

[AllowAnonymous]
public class LoginTotpModel(
    AuthDbContext db,
    ITotpService totp,
    IUserAccountService userAccountService,
    IGlobalAuthenticationService globalAuthenticationService,
    ILoginRateLimiter loginRateLimiter,
    ILogger<LoginTotpModel> logger) : PageModel
{
    [BindProperty]
    [Required, StringLength(6, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Display { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var preauth = await HttpContext.AuthenticateAsync("preauth");
        if (!preauth.Succeeded)
            return RedirectToPage("/Login", new { ReturnUrl, Display });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var preauth = await HttpContext.AuthenticateAsync("preauth");
        if (!preauth.Succeeded)
            return RedirectToPage("/Login", new { ReturnUrl, Display });

        var sub = preauth.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId))
            return RedirectToPage("/Login", new { ReturnUrl, Display });

        // Get the per-tenant user to look up the linked UserAccount
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || string.IsNullOrEmpty(user.Email))
            return RedirectToPage("/Login", new { ReturnUrl, Display });

        // Get MFA settings from UserAccount (global)
        var account = await userAccountService.FindByEmailAsync(user.Email);
        if (account is null)
            return RedirectToPage("/Login", new { ReturnUrl, Display });

        var (mfaEnabled, totpSecret) = await userAccountService.GetMfaStatusAsync(account.Id);
        if (!mfaEnabled || string.IsNullOrEmpty(totpSecret))
            return RedirectToPage("/Login", new { ReturnUrl, Display });

        // Rate-limit the second factor. Without this, an attacker who already has a valid password
        // (and thus a preauth cookie) could brute-force the 6-digit TOTP, and the sliding preauth
        // cookie would keep their session alive across attempts.
        if (await loginRateLimiter.IsLockedOutAsync(HttpContext, user.Username, HttpContext.RequestAborted))
        {
            logger.LogWarning("MFA rate limit triggered for user {User}", user.Username);
            ModelState.AddModelError(string.Empty, "Too many failed attempts. Please try again later.");
            return Page();
        }

        if (!totp.VerifyCode(totpSecret, Code, digits: 6, period: 30, window: 1))
        {
            await loginRateLimiter.RegisterFailedAttemptAsync(HttpContext, user.Username, HttpContext.RequestAborted);
            ModelState.AddModelError(string.Empty, "Invalid code");
            return Page();
        }

        await loginRateLimiter.ClearAsync(HttpContext, user.Username, HttpContext.RequestAborted);
        await globalAuthenticationService.ClearFailedAttemptsAsync(account.Id);

        var preauthAmrValues = preauth.Principal?.FindAll(OidcConstants.Claims.Amr)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        await HttpContext.SignOutAsync("preauth");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new(OidcConstants.Claims.Acr, OidcConstants.AcrValues.Mfa),
            new(OidcConstants.Claims.Idp, "local")
        };

        foreach (var amr in preauthAmrValues)
        {
            claims.Add(new(OidcConstants.Claims.Amr, amr));
        }
        claims.Add(new(OidcConstants.Claims.Amr, "mfa"));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    HttpContext.Session.Clear();
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        logger.LogInformation("User {User} finished MFA (validated against global UserAccount)", user.Username);

        var postAuthenticationReturnUrl = AuthorizeReturnUrlHelper.ConsumePromptValues(ReturnUrl, "login", "select_account");

        if (!string.IsNullOrEmpty(postAuthenticationReturnUrl) && Url.IsLocalUrl(postAuthenticationReturnUrl))
        {
            return LocalRedirect(postAuthenticationReturnUrl);
        }

        return RedirectToPage("/Index");
    }
}
