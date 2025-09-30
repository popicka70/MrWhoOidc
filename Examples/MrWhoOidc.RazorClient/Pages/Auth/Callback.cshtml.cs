using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Client.Authorization;
using MrWhoOidc.Client.Tokens;

namespace MrWhoOidc.RazorClient.Pages.Auth;

public class CallbackModel : PageModel
{
    private readonly IMrWhoAuthorizationManager _authorizationManager;
    private readonly IMrWhoTokenClient _tokenClient;
    private readonly ILogger<CallbackModel> _logger;

    public CallbackModel(IMrWhoAuthorizationManager authorizationManager, IMrWhoTokenClient tokenClient, ILogger<CallbackModel> logger)
    {
        _authorizationManager = authorizationManager;
        _tokenClient = tokenClient;
        _logger = logger;
    }

    public string? Error { get; private set; }
    public string? ErrorDescription { get; private set; }

    public async Task<IActionResult> OnGetAsync(string state, string? code = null, string? error = null, string? error_description = null, string? response = null, string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        if (string.IsNullOrWhiteSpace(state))
        {
            Error = "invalid_state";
            ErrorDescription = "Missing state parameter.";
            return Page();
        }

        var validation = await _authorizationManager.ValidateCallbackAsync(state, code, error, response, HttpContext.RequestAborted).ConfigureAwait(false);
        if (validation.IsError)
        {
            Error = validation.Error ?? error;
            ErrorDescription = validation.ErrorDescription ?? error_description;
            if (!string.IsNullOrEmpty(validation.ErrorUri))
            {
                ViewData["ErrorUri"] = validation.ErrorUri;
            }
            if (validation.IsJarmResponse && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Received JARM error response: {Jwt}", validation.ResponseJwt);
            }
            return Page();
        }

        if (validation.IsJarmResponse && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Validated JARM success response: {Jwt}", validation.ResponseJwt);
        }

        if (string.IsNullOrEmpty(validation.Code))
        {
            Error = "invalid_grant";
            ErrorDescription = "Authorization code missing from callback.";
            return Page();
        }

        var callbackUrl = Url.Page("/Auth/Callback", pageHandler: null, values: new { returnUrl }, protocol: Request.Scheme, host: Request.Host.ToString());
        if (string.IsNullOrEmpty(callbackUrl))
        {
            Error = "callback_error";
            ErrorDescription = "Unable to resolve callback URL.";
            return Page();
        }

        var tokenResult = await _tokenClient.ExchangeCodeAsync(validation.Code, new Uri(callbackUrl, UriKind.Absolute), validation.CodeVerifier, HttpContext.RequestAborted).ConfigureAwait(false);
        if (tokenResult.IsError)
        {
            Error = tokenResult.Error;
            ErrorDescription = tokenResult.ErrorDescription;
            _logger.LogWarning("Token exchange failed: {Error} {Description}", tokenResult.Error, tokenResult.ErrorDescription);
            return Page();
        }

        ClaimsPrincipal principal;
        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(tokenResult.IdToken))
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokenResult.IdToken);

            if (!string.IsNullOrEmpty(validation.Nonce))
            {
                var nonceClaim = jwt.Claims.FirstOrDefault(c => string.Equals(c.Type, "nonce", StringComparison.Ordinal))?.Value;
                if (!string.Equals(nonceClaim, validation.Nonce, StringComparison.Ordinal))
                {
                    Error = "invalid_nonce";
                    ErrorDescription = "The nonce in the ID token did not match the request.";
                    return Page();
                }
            }

            claims.AddRange(jwt.Claims);
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, "unknown"));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        principal = new ClaimsPrincipal(identity);

        var tokens = new List<AuthenticationToken>();
        if (!string.IsNullOrEmpty(tokenResult.AccessToken))
        {
            tokens.Add(new AuthenticationToken { Name = "access_token", Value = tokenResult.AccessToken });
        }
        if (!string.IsNullOrEmpty(tokenResult.RefreshToken))
        {
            tokens.Add(new AuthenticationToken { Name = "refresh_token", Value = tokenResult.RefreshToken });
        }
        if (!string.IsNullOrEmpty(tokenResult.IdToken))
        {
            tokens.Add(new AuthenticationToken { Name = "id_token", Value = tokenResult.IdToken });
        }
        if (!string.IsNullOrEmpty(tokenResult.TokenType))
        {
            tokens.Add(new AuthenticationToken { Name = "token_type", Value = tokenResult.TokenType });
        }
        if (tokenResult.ExpiresIn is long expiresIn)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("O", CultureInfo.InvariantCulture);
            tokens.Add(new AuthenticationToken { Name = "expires_at", Value = expiresAt });
        }
        if (!string.IsNullOrEmpty(tokenResult.Scope))
        {
            tokens.Add(new AuthenticationToken { Name = "scope", Value = tokenResult.Scope });
        }

        var authProperties = new AuthenticationProperties
        {
            RedirectUri = returnUrl,
            IsPersistent = true
        };
        authProperties.StoreTokens(tokens);

        var displayName = principal.Identity?.Name ?? principal.FindFirstValue("name") ?? principal.FindFirstValue("sub") ?? "user";
        authProperties.Items["display_name"] = displayName;

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties).ConfigureAwait(false);

        _logger.LogInformation("Successfully signed in {DisplayName}", displayName);
        return Redirect(returnUrl);
    }
}
