using Microsoft.Extensions.Logging;
using MrWhoOidc.WebAuth.Handlers; // for OidcOptions
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Handles the client_credentials grant (extracted from TokenHandler).
/// Validates audience/resource, parses scopes, then delegates to ITokenService.
/// Metrics are emitted centrally by TokenHandler after GrantExecutionResult.
/// </summary>
public sealed class ClientCredentialsGrantHandler(ILogger<ClientCredentialsGrantHandler> logger) : ITokenGrantHandler
{
    public string GrantType => "client_credentials";

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new GrantExecutionResult(false, false, null);

        var form = context.Form;
        var aud = form["audience"].ToString();
        var resource = form["resource"].ToString();
        if (!string.IsNullOrEmpty(aud) && !string.IsNullOrEmpty(resource) && !string.Equals(aud, resource, StringComparison.Ordinal))
        {
            logger.LogWarning("/token invalid_request: audience/resource conflict for client {ClientIdHash}", Infrastructure.Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidRequest("audience and resource conflict"));
        }
        var audience = !string.IsNullOrEmpty(resource) ? resource : (!string.IsNullOrEmpty(aud) ? aud : "api");

        var scopeParam = form["scope"].ToString();
        var requestedScopes = string.IsNullOrWhiteSpace(scopeParam) ? System.Array.Empty<string>() : scopeParam.Split(' ', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        var issuer = context.Options.Issuer ?? ($"{context.Http.Request.Scheme}://{context.Http.Request.Host}");
        var (ok, payload, _, status) = await context.Tokens.CreateClientCredentialsTokenAsync(context.ClientId, audience, requestedScopes, issuer, context.DPoPJkt);
        if (!ok)
        {
            logger.LogWarning("/token client_credentials issuance failed for client {ClientIdHash}", Infrastructure.Bucketization.Bucket(context.ClientId));
        }
        var result = Microsoft.AspNetCore.Http.Results.Json(payload!, statusCode: status);
        return new GrantExecutionResult(true, ok, result);
    }
}
