using MrWhoOidc.Auth.Services.Authentication;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Authenticates clients for introspection requests through the shared <see cref="IClientAuthenticator"/>:
/// one method per request, Basic/form client_id match, the registered token_endpoint_auth_method, and
/// the introspection mTLS allow-lists (per client or <c>AuthOptions.IntrospectionMtlsCertificates</c>;
/// when configured, mTLS is required and sufficient). Public clients are rejected (RFC 7662 §2.1).
/// </summary>
public sealed class ClientAuthenticator(
    IClientAuthenticator clientAuthenticator,
    ILogger<ClientAuthenticator> logger)
{
    public async Task<(bool Authenticated, IResult? ErrorResult)> AuthenticateAsync(IntrospectionContext context)
    {
        var http = context.HttpContext;
        var result = await clientAuthenticator.AuthenticateAsync(http, new ClientAuthenticationContext
        {
            Usage = ClientAuthenticationUsage.Introspection,
            AdditionalAudiences = [context.Endpoint, context.Issuer],
            RequireConfidentialClient = true
        }).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            logger.LogWarning("Introspection client authentication failed for client {ClientBucket}", context.ClientBucket);
            return (false, result.ErrorResult ?? ErrorResults.InvalidClient(http));
        }

        // The handler loaded the client from the parsed client_id; the authenticated client must be that one.
        if (!string.Equals(result.Client?.ClientId, context.Client.ClientId, StringComparison.Ordinal))
        {
            logger.LogWarning("Introspection client authentication resolved a different client for {ClientBucket}", context.ClientBucket);
            return (false, ErrorResults.InvalidClient(http));
        }

        return (true, null);
    }
}
