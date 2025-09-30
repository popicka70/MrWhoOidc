using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Logout;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.RazorClient.Pages.Auth;

public class LogoutModel : PageModel
{
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly ILogger<LogoutModel> _logger;
    private readonly IMrWhoLogoutManager _logoutManager;

    public LogoutModel(IOptionsMonitor<MrWhoOidcClientOptions> options, ILogger<LogoutModel> logger, IMrWhoLogoutManager logoutManager)
    {
        _options = options;
        _logger = logger;
        _logoutManager = logoutManager;
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
            var idToken = await HttpContext.GetTokenAsync("id_token").ConfigureAwait(false);
            var sid = User?.FindFirst("sid")?.Value;
            var absoluteReturn = new Uri($"{Request.Scheme}://{Request.Host}{returnUrl}");

            var options = new FrontChannelLogoutOptions
            {
                PostLogoutRedirectUri = absoluteReturn,
                IdTokenHint = string.IsNullOrEmpty(idToken) ? null : idToken,
                Sid = sid
            };

            options.AdditionalParameters["returnUrl"] = returnUrl;

            var request = await _logoutManager.BuildFrontChannelLogoutAsync(options).ConfigureAwait(false);
            return request.LogoutUri.ToString();
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

        if (!opts.Logout.EnableFrontChannel)
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
