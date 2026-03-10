using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers; // for OidcOptions
using MrWhoOidc.WebAuth.Extensions; // for GetIssuer
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Handles the refresh_token grant. Mirrors previous inline logic from TokenHandler.
/// </summary>
public sealed class RefreshTokenGrantHandler(ILogger<RefreshTokenGrantHandler> logger) : ITokenGrantHandler
{
    public string GrantType => OAuthConstants.GrantTypes.RefreshToken;

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new GrantExecutionResult(false, false, null);

        var refresh = context.Form[OAuthConstants.Parameters.RefreshToken].ToString();
        var audience = context.Form[OAuthConstants.Parameters.Audience].ToString();
        var resource = context.Form[OAuthConstants.Parameters.Resource].ToString();
        if (string.IsNullOrWhiteSpace(refresh))
        {
            logger.LogWarning("/token invalid_request: missing refresh_token for client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
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

        (bool ok, object? payload, string? _, int status) = await context.Tokens.ExchangeRefreshTokenAsync(
            refresh,
            context.ClientId,
            issuer,
            context.DPoPJkt,
            ipAddress,
            userAgent,
            resourceOverride,
            context.TenantId);
        if (!ok)
        {
            logger.LogWarning("/token refresh_token exchange failed for client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
        }
        var result = Microsoft.AspNetCore.Http.Results.Json(payload!, statusCode: status);
        return new GrantExecutionResult(true, ok, result);
    }
}
