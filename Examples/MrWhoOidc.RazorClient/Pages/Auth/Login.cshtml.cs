using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Client.Authorization;

namespace MrWhoOidc.RazorClient.Pages.Auth;

public class LoginModel : PageModel
{
    private readonly IMrWhoAuthorizationManager _authorizationManager;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IMrWhoAuthorizationManager authorizationManager, ILogger<LoginModel> logger)
    {
        _authorizationManager = authorizationManager;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");
        var callbackUrl = Url.Page("/Auth/Callback", pageHandler: null, values: new { returnUrl }, protocol: Request.Scheme, host: Request.Host.ToString());
        if (string.IsNullOrEmpty(callbackUrl))
        {
            _logger.LogWarning("Unable to determine callback URL for login.");
            return BadRequest("Callback URL could not be resolved.");
        }

        var context = await _authorizationManager.BuildAuthorizeRequestAsync(new Uri(callbackUrl, UriKind.Absolute), cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);

        _logger.LogInformation("Redirecting to authorization server for state {State}", context.State);
        return Redirect(context.RequestUri.ToString());
    }
}
