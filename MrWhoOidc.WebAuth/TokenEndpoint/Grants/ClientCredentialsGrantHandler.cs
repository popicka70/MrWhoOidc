using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.WebAuth.Handlers; // for OidcOptions
using MrWhoOidc.WebAuth.Extensions; // for GetIssuer
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Utils;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Handles the client_credentials grant (extracted from TokenHandler).
/// Validates audience/resource, parses scopes, then delegates to ITokenService.
/// Metrics are emitted centrally by TokenHandler after GrantExecutionResult.
/// </summary>
public sealed class ClientCredentialsGrantHandler(ILogger<ClientCredentialsGrantHandler> logger, MrWhoOidc.Auth.Services.IMtlsThumbprintResolver mtlsResolver) : ITokenGrantHandler
{
    public string GrantType => OAuthConstants.GrantTypes.ClientCredentials;

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new GrantExecutionResult(false, false, null);

        var form = context.Form;
        var aud = form[OAuthConstants.Parameters.Audience].ToString();
        var resource = form[OAuthConstants.Parameters.Resource].ToString();
        if (!string.IsNullOrEmpty(aud) && !string.IsNullOrEmpty(resource) && !string.Equals(aud, resource, StringComparison.Ordinal))
        {
            logger.LogWarning("/token invalid_request: audience/resource conflict for client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidRequest("audience and resource conflict"));
        }
        var audience = !string.IsNullOrEmpty(resource) ? resource : (!string.IsNullOrEmpty(aud) ? aud : null);
        if (string.IsNullOrEmpty(audience))
        {
            logger.LogWarning("/token invalid_request: missing audience/resource for client_credentials client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResults.InvalidRequest("audience or resource is required for client_credentials"));
        }

        var scopeParam = form[OAuthConstants.Parameters.Scope].ToString();
        var requestedScopes = string.IsNullOrWhiteSpace(scopeParam) ? System.Array.Empty<string>() : scopeParam.Split(' ', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        // Fail-closed for product scopes on client_credentials.
        if (requestedScopes.Any(ProductScopeClassifier.IsProductScope))
        {
            return new GrantExecutionResult(true, false, ErrorResults.InvalidScope("product scopes are not supported for client_credentials"));
        }

        var issuer = context.Http.GetIssuer(context.Options);
        // If client presented a certificate, resolve its x5t#S256 thumbprint for CB-TLS
        string? mtlsX5tS256 = null;
        var cert = context.Http.Connection.ClientCertificate;
        if (cert != null)
        {
            mtlsX5tS256 = mtlsResolver.ResolveThumbprint(cert);
        }
        var (ok, payload, _, status) = await context.Tokens.CreateClientCredentialsTokenAsync(context.ClientId, audience, requestedScopes, issuer, context.DPoPJkt, mtlsX5tS256);
        if (!ok)
        {
            logger.LogWarning("/token client_credentials issuance failed for client {ClientIdHash}", Bucketization.Bucket(context.ClientId));
        }
        var result = Microsoft.AspNetCore.Http.Results.Json(payload!, statusCode: status);
        return new GrantExecutionResult(true, ok, result);
    }
}
