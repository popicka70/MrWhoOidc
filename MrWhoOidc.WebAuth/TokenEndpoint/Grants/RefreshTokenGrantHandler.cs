using Microsoft.Extensions.Logging;
using MrWhoOidc.WebAuth.Handlers; // for OidcOptions
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
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
        if (string.IsNullOrWhiteSpace(refresh))
        {
            logger.LogWarning("/token invalid_request: missing refresh_token for client {ClientIdHash}", Infrastructure.Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidRequest());
        }

        var issuer = context.Options.Issuer ?? ($"{context.Http.Request.Scheme}://{context.Http.Request.Host}");
        (bool ok, object? payload, string? _, int status) = await context.Tokens.ExchangeRefreshTokenAsync(refresh, context.ClientId, issuer, context.DPoPJkt);
        if (!ok)
        {
            logger.LogWarning("/token refresh_token exchange failed for client {ClientIdHash}", Infrastructure.Bucketization.Bucket(context.ClientId));
        }
        var result = Microsoft.AspNetCore.Http.Results.Json(payload!, statusCode: status);
        return new GrantExecutionResult(true, ok, result);
    }
}
