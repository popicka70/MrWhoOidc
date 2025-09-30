using System.Collections.Generic;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.RazorClient.Pages.Auth;

public class LogoutModel : PageModel
{
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly ILogger<LogoutModel> _logger;

    public LogoutModel(IOptionsMonitor<MrWhoOidcClientOptions> options, ILogger<LogoutModel> logger)
    {
        _options = options;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool CanFederatedSignOut { get; private set; }

    public void OnGet()
    {
        ReturnUrl = NormalizeReturnUrl(ReturnUrl);
        CanFederatedSignOut = EvaluateFederatedCapability();
    }

    public async Task<IActionResult> OnPostAsync(string mode = "local", string? returnUrl = null)
    {
        var normalizedReturn = NormalizeReturnUrl(returnUrl ?? ReturnUrl);

        if (string.Equals(mode, "federated", StringComparison.OrdinalIgnoreCase))
        {
            var redirect = await BuildFederatedLogoutRedirectAsync(normalizedReturn).ConfigureAwait(false);
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(redirect))
            {
                return Redirect(redirect);
            }
            _logger.LogWarning("Falling back to local logout after failing to build federated redirect.");
            return LocalRedirect(normalizedReturn);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        return LocalRedirect(normalizedReturn);
    }

    private async Task<string?> BuildFederatedLogoutRedirectAsync(string returnUrl)
    {
        try
        {
            var opts = _options.CurrentValue;
            if (string.IsNullOrWhiteSpace(opts.Issuer) || string.IsNullOrWhiteSpace(opts.ClientId))
            {
                _logger.LogWarning("Federated logout not available due to missing issuer or client identifier.");
                return null;
            }

            if (!Uri.TryCreate(opts.Issuer, UriKind.Absolute, out var issuer))
            {
                _logger.LogWarning("Federated logout not available because issuer '{Issuer}' is not a valid absolute URI.", opts.Issuer);
                return null;
            }

            var authority = issuer.GetLeftPart(UriPartial.Authority) + issuer.AbsolutePath.TrimEnd('/');
            if (!authority.EndsWith("/", StringComparison.Ordinal))
            {
                authority += "/";
            }

            var logoutEndpoint = authority + "logout";
            var absoluteReturn = $"{Request.Scheme}://{Request.Host}{returnUrl}";

            var query = new Dictionary<string, string?>
            {
                ["returnUrl"] = returnUrl,
                ["client_id"] = opts.ClientId,
                ["post_logout_redirect_uri"] = absoluteReturn
            };

            var idToken = await HttpContext.GetTokenAsync("id_token").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(idToken))
            {
                query["id_token_hint"] = idToken;
            }

            var sid = User?.FindFirst("sid")?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                query["sid"] = sid;
            }

            return QueryHelpers.AddQueryString(logoutEndpoint, query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while building federated logout redirect.");
            return null;
        }
    }

    private bool EvaluateFederatedCapability()
    {
        var opts = _options.CurrentValue;
        if (User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(opts.Issuer) || string.IsNullOrWhiteSpace(opts.ClientId))
        {
            return false;
        }

        return Uri.TryCreate(opts.Issuer, UriKind.Absolute, out _);
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        if (returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return "/";
        }

        if (returnUrl.StartsWith("~/", StringComparison.Ordinal))
        {
            return "/";
        }

        if (Uri.TryCreate(returnUrl, UriKind.Relative, out _))
        {
            return returnUrl.StartsWith("/", StringComparison.Ordinal) ? returnUrl : "/" + returnUrl.TrimStart('/');
        }

        return "/";
    }
}
