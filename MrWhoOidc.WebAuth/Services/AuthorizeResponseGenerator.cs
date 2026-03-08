using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Infrastructure.Http;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Extensions;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.DataProtection;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Generates OIDC-compliant HTTP responses for the authorization endpoint.
/// </summary>
public interface IAuthorizeResponseGenerator
{
    /// <summary>
    /// Creates a success response (e.g., redirect with code, JARM, or Form Post).
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="validation">The validated request details.</param>
    /// <param name="code">The generated authorization code.</param>
    /// <param name="redirectUri">The redirect URI to use.</param>
    /// <returns>An IResult representing the success response.</returns>
    IResult CreateSuccessResponse(HttpContext http, AuthorizeValidationResult validation, string code, string? redirectUri);

    /// <summary>
    /// Creates an error response (e.g., redirect with error params or local error page).
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="validation">The validation result containing error details.</param>
    /// <param name="correlationId">The correlation ID for tracking.</param>
    /// <returns>An IResult representing the error response.</returns>
    IResult CreateErrorResponse(HttpContext http, AuthorizeValidationResult validation, string correlationId);

    /// <summary>
    /// Creates a redirect to the consent page.
    /// </summary>
    /// <param name="http">The current HTTP context.</param>
    /// <param name="validation">The validated request details.</param>
    /// <param name="consentUrl">The URL of the consent page.</param>
    /// <returns>An IResult representing the redirect to consent.</returns>
    IResult CreateConsentRedirect(HttpContext http, AuthorizeValidationResult validation, string consentUrl);
}

public sealed class AuthorizeResponseGenerator(IJarmService jarm, IDataProtectionProvider dataProtection) : IAuthorizeResponseGenerator
{
    private const string OpBrowserStateCookieName = "mrwho.opbs";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MrWhoOidc.WebAuth.Pages.Auth.Redirect");

    public IResult CreateErrorResponse(HttpContext http, AuthorizeValidationResult validation, string correlationId)
    {
        var issuer = http.GetIssuer();
        if (!string.IsNullOrEmpty(validation.RedirectUri))
        {
            if (string.Equals(validation.ResponseMode, OidcConstants.ResponseModes.QueryJwt, StringComparison.Ordinal) ||
                string.Equals(validation.ResponseMode, OidcConstants.ResponseModes.FragmentJwt, StringComparison.Ordinal) ||
                string.Equals(validation.ResponseMode, OidcConstants.ResponseModes.FormPostJwt, StringComparison.Ordinal))
            {
                var jarmJwt = jarm.CreateErrorResponseAsync(validation.ClientId!, issuer, validation.Error!, $"{validation.ErrorDescription} (corr={correlationId})", validation.State).GetAwaiter().GetResult();
                return JarmRedirect(validation.RedirectUri, validation.ResponseMode, jarmJwt, sessionState: null);
            }

            var uri = new UriBuilder(validation.RedirectUri);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query["error"] = validation.Error;
            query["error_description"] = $"{validation.ErrorDescription} (corr={correlationId})";
            if (!string.IsNullOrEmpty(validation.State)) query["state"] = validation.State;
            uri.Query = query.ToString();
            return Results.Redirect(uri.ToString());
        }

        return ErrorResults.InvalidRequest($"{validation.ErrorDescription} (corr={correlationId})");
    }

    public IResult CreateSuccessResponse(HttpContext http, AuthorizeValidationResult validation, string code, string? redirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri)) return Results.BadRequest("Missing redirect URI");

        var issuer = http.GetIssuer();
        var sessionState = TryCreateSessionState(http, validation.ClientId, redirectUri);

        if (string.Equals(validation.ResponseMode, OidcConstants.ResponseModes.QueryJwt, StringComparison.Ordinal) ||
            string.Equals(validation.ResponseMode, OidcConstants.ResponseModes.FragmentJwt, StringComparison.Ordinal) ||
            string.Equals(validation.ResponseMode, OidcConstants.ResponseModes.FormPostJwt, StringComparison.Ordinal))
        {
            var jarmJwt = jarm.CreateSuccessResponseAsync(validation.ClientId!, issuer, code, validation.ResponseMode!, validation.State).GetAwaiter().GetResult();
            return JarmRedirect(redirectUri, validation.ResponseMode!, jarmJwt, sessionState);
        }

        var uri = new UriBuilder(redirectUri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["iss"] = issuer;
        if (!string.IsNullOrWhiteSpace(sessionState))
        {
            query["session_state"] = sessionState;
        }
        // State is handled by the caller or already in the redirectUri if it was a PAR/JAR request
        uri.Query = query.ToString();

        var protectedUrl = _protector.Protect(uri.ToString());
        return Results.Redirect($"/Auth/Redirect?redirectUrl={Uri.EscapeDataString(protectedUrl)}");
    }

    public IResult CreateConsentRedirect(HttpContext http, AuthorizeValidationResult validation, string consentUrl)
    {
        var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
        var scopesQuery = string.Join("&", (validation.Scopes ?? Array.Empty<string>()).Select(s => $"Scopes={Uri.EscapeDataString(s)}"));
        var finalUrl = $"{consentUrl}?ClientId={Uri.EscapeDataString(validation.ClientId!)}&ReturnUrl={Uri.EscapeDataString(returnUrl)}&{scopesQuery}";
        return Results.Redirect(finalUrl);
    }

    private IResult JarmRedirect(string redirectUri, string? responseMode, string jwt, string? sessionState)
    {
        if (string.Equals(responseMode, OidcConstants.ResponseModes.FormPostJwt, StringComparison.Ordinal))
        {
            // NOTE: this project currently renders a simplified FormPost result (and may not have a corresponding view).
            // Include session_state when present for OIDC session management compatibility.
            return Results.Extensions.RazorPage("/FormPost", new { redirectUri, response = jwt, session_state = sessionState });
        }

        if (string.Equals(responseMode, OidcConstants.ResponseModes.FragmentJwt, StringComparison.Ordinal))
        {
            var fragmentUri = new UriBuilder(redirectUri);
            var fragment = System.Web.HttpUtility.ParseQueryString(fragmentUri.Fragment.TrimStart('#'));
            fragment["response"] = jwt;
            if (!string.IsNullOrWhiteSpace(sessionState))
            {
                fragment["session_state"] = sessionState;
            }
            fragmentUri.Fragment = fragment.ToString();
            return Results.Redirect(fragmentUri.ToString());
        }

        var uri = new UriBuilder(redirectUri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["response"] = jwt;
        if (!string.IsNullOrWhiteSpace(sessionState))
        {
            query["session_state"] = sessionState;
        }
        uri.Query = query.ToString();
        return Results.Redirect(uri.ToString());
    }

    private static string? TryCreateSessionState(HttpContext http, string? clientId, string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return null;

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var opbs = EnsureOpBrowserStateCookie(http);
        var salt = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));

        var input = $"{clientId} {origin} {opbs} {salt}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hashB64Url = Base64UrlEncoder.Encode(hash);
        return $"{hashB64Url}.{salt}";
    }

    private static string EnsureOpBrowserStateCookie(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(OpBrowserStateCookieName, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var value = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(
            OpBrowserStateCookieName,
            value,
            new CookieOptions
            {
                Path = "/",
                HttpOnly = false, // JS in check_session_iframe must read this value
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.None,
                IsEssential = true
            });
        return value;
    }
}
