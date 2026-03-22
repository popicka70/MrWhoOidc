using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Client.Authorization;

namespace MrWhoOidc.RazorClient.Pages.Auth;

public class LoginModel : PageModel
{
    private const string ReturnUrlCookiePrefix = "mrwho-razorclient-return-url-";
    private readonly IMrWhoAuthorizationManager _authorizationManager;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IMrWhoAuthorizationManager authorizationManager, ILogger<LoginModel> logger)
    {
        _authorizationManager = authorizationManager;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, string? mode = null)
    {
        returnUrl = NormalizeReturnUrl(returnUrl);
        var callbackUrl = Url.Page("/Auth/Callback", pageHandler: null, values: null, protocol: Request.Scheme, host: Request.Host.ToString());
        if (string.IsNullOrEmpty(callbackUrl))
        {
            _logger.LogWarning("Unable to determine callback URL for login.");
            return BadRequest("Callback URL could not be resolved.");
        }

        var useJarFlow = string.Equals(mode, "jar", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(mode))
        {
            ViewData["AuthMode"] = mode;
        }

        var context = await _authorizationManager.BuildAuthorizeRequestAsync(
            new Uri(callbackUrl, UriKind.Absolute),
            options =>
            {
                if (useJarFlow)
                {
                    options.UseJar = true;
                    options.UseJarm = true;
                }
            },
            HttpContext.RequestAborted).ConfigureAwait(false);

        Response.Cookies.Append(
            ReturnUrlCookiePrefix + context.State,
            returnUrl,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10)
            });

        _logger.LogInformation("Redirecting to authorization server for state {State}", context.State);
        return Redirect(context.RequestUri.ToString());
    }

    private string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return Url.Content("~/");
        }

        if (returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return Url.Content("~/");
        }

        if (returnUrl.StartsWith("~/", StringComparison.Ordinal))
        {
            return Url.Content(returnUrl);
        }

        if (Uri.TryCreate(returnUrl, UriKind.Relative, out _))
        {
            return returnUrl.StartsWith("/", StringComparison.Ordinal) ? returnUrl : "/" + returnUrl.TrimStart('/');
        }

        return Url.Content("~/");
    }
}
