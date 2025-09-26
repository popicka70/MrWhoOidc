using Microsoft.Extensions.Logging;
using MrWhoOidc.WebAuth.Handlers; // for OidcOptions
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Handles the authorization_code grant (previously inline in TokenHandler).
/// Performs parameter validation then delegates to ITokenService.
/// </summary>
public sealed class AuthorizationCodeGrantHandler(ILogger<AuthorizationCodeGrantHandler> logger) : ITokenGrantHandler
{
    public string GrantType => "authorization_code";

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new GrantExecutionResult(false, false, null);

        var code = context.Form["code"].ToString();
        var redirectUri = context.Form["redirect_uri"].ToString();
        var codeVerifier = context.Form["code_verifier"].ToString();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri))
        {
            logger.LogWarning("/token invalid_request: missing code or redirect_uri for client {ClientIdHash}", Infrastructure.Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidRequest());
        }

        var issuer = context.Options.Issuer ?? ($"{context.Http.Request.Scheme}://{context.Http.Request.Host}");
        var (ok, payload, _, status) = await context.Tokens.ExchangeAuthorizationCodeAsync(code, redirectUri, context.ClientId, codeVerifier, issuer, context.DPoPJkt);
        if (!ok)
        {
            logger.LogWarning("/token authorization_code exchange failed for client {ClientIdHash}", Infrastructure.Bucketization.Bucket(context.ClientId));
        }
        var result = Microsoft.AspNetCore.Http.Results.Json(payload!, statusCode: status);
        return new GrantExecutionResult(true, ok, result);
    }
}
