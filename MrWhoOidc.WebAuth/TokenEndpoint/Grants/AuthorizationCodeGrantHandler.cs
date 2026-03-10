using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers; // for OidcOptions
using MrWhoOidc.WebAuth.Extensions; // for GetIssuer
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Handles the authorization_code grant (previously inline in TokenHandler).
/// Performs parameter validation then delegates to ITokenService.
/// </summary>
public sealed class AuthorizationCodeGrantHandler(ILogger<AuthorizationCodeGrantHandler> logger) : ITokenGrantHandler
{
    public string GrantType => OAuthConstants.GrantTypes.AuthorizationCode;

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new GrantExecutionResult(false, false, null);

        var code = context.Form[OAuthConstants.Parameters.Code].ToString();
        var redirectUri = context.Form[OAuthConstants.Parameters.RedirectUri].ToString();
        var codeVerifier = context.Form[OAuthConstants.Parameters.CodeVerifier].ToString();
        var audience = context.Form[OAuthConstants.Parameters.Audience].ToString();
        var resource = context.Form[OAuthConstants.Parameters.Resource].ToString();
        var claimsJson = context.Form[OAuthConstants.Parameters.Claims].ToString();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri))
        {
            logger.LogWarning("/token {ErrorCode}: missing code or redirect_uri for client {ClientIdHash}",
                OAuthConstants.ErrorCodes.InvalidRequest, Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidRequest());
        }

        if (!string.IsNullOrEmpty(audience) && !string.IsNullOrEmpty(resource) && !string.Equals(audience, resource, StringComparison.Ordinal))
        {
            logger.LogWarning("/token invalid_request: audience/resource conflict for client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidRequest("audience and resource conflict"));
        }

        var resourceOverride = !string.IsNullOrEmpty(resource) ? resource : audience;
        if (!string.IsNullOrWhiteSpace(resourceOverride) && !Uri.TryCreate(resourceOverride, UriKind.Absolute, out _))
        {
            logger.LogWarning("/token invalid_target: non-absolute resource for client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidTarget("resource must be an absolute URI"));
        }

        var issuer = context.Http.GetIssuer(context.Options);

        // Capture session metadata
        var ipAddress = context.Http.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Http.Request.Headers.UserAgent.ToString();

        var claimsOverride = string.IsNullOrWhiteSpace(claimsJson) ? null : claimsJson;

        var (ok, payload, _, status) = await context.Tokens.ExchangeAuthorizationCodeAsync(
            code,
            redirectUri,
            context.ClientId,
            codeVerifier,
            issuer,
            context.DPoPJkt,
            ipAddress,
            userAgent,
            resourceOverride,
            claimsOverride,
            context.TenantId);
        if (!ok)
        {
            logger.LogWarning("/token authorization_code exchange failed for client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
        }
        var result = Microsoft.AspNetCore.Http.Results.Json(payload!, statusCode: status);
        return new GrantExecutionResult(true, ok, result);
    }
}
