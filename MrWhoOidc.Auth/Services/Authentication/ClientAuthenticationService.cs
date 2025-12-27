using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;
using System.Text.Json;

namespace MrWhoOidc.Auth.Services.Authentication;

/// <summary>
/// Implementation of IClientAuthenticationService that handles domain-level client authentication logic.
/// </summary>
public sealed class ClientAuthenticationService(
    IClientStore clientStore,
    IClientAssertionValidator assertionValidator,
    IOptions<AuthOptions> authOptions,
    ILogger<ClientAuthenticationService> logger) : IClientAuthenticationService
{
    public async Task<ClientAuthResult> AuthenticateAsync(ClientCredentialInput input, CancellationToken ct = default)
    {
        // 1. Load Client
        var client = await clientStore.FindByClientIdAsync(input.ClientId, ct).ConfigureAwait(false);
        if (client is null)
        {
            logger.LogWarning("Client authentication failed: Unknown client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
            return new ClientAuthResult(false, null, "unauthorized_client", "Unknown client");
        }

        // 2. mTLS Checks
        if (ShouldCheckMtls(input, client))
        {
            var allowedThumbprints = GetAllowedMtlsThumbprints(input, client);
            if (allowedThumbprints is { Length: > 0 })
            {
                var presented = input.MtlsThumbprint;
                var ok = !string.IsNullOrEmpty(presented) && allowedThumbprints.Any(a => string.Equals(a, presented, StringComparison.OrdinalIgnoreCase));
                if (!ok)
                {
                    logger.LogWarning("Client authentication failed: mTLS required but missing/invalid for client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                    return new ClientAuthResult(false, client, "invalid_client", "mtls_required");
                }
            }
        }

        // 3. Authenticate (Secret or Assertion)
        bool authenticated = false;

        if (string.Equals(input.ClientAssertionType, OAuthConstants.ClientAssertionTypes.JwtBearer, StringComparison.Ordinal) && !string.IsNullOrEmpty(input.ClientAssertion))
        {
            if (!client.AllowPrivateKeyJwt)
            {
                logger.LogWarning("Client authentication failed: private_key_jwt disabled for client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                return new ClientAuthResult(false, client, "unauthorized_client", "private_key_jwt disabled");
            }
            
            authenticated = await assertionValidator.ValidateAsync(client.ClientId, input.ClientAssertion, input.EndpointUrl ?? string.Empty).ConfigureAwait(false);
            if (!authenticated)
            {
                logger.LogWarning("Client authentication failed: private_key_jwt validation failed for client {ClientIdHash}", Bucketization.Bucket(client.ClientId));
            }
        }
        else
        {
            // Client Secret
            // Check policies if Client Credentials Grant
            if (input.Usage == ClientAuthenticationUsage.TokenEndpoint && 
                string.Equals(input.GrantType, OAuthConstants.GrantTypes.ClientCredentials, StringComparison.Ordinal))
            {
#pragma warning disable CS0618
                if (string.IsNullOrEmpty(client.ClientSecretHash))
                {
                    logger.LogWarning("Client authentication failed: public client not allowed for client_credentials {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                    return new ClientAuthResult(false, client, "unauthorized_client");
                }
#pragma warning restore CS0618
            }

            authenticated = await clientStore.ValidateClientSecretAsync(input.ClientId, input.ClientSecret, ct).ConfigureAwait(false);
            if (!authenticated)
            {
                logger.LogWarning("Client authentication failed: secret validation failed for client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
            }
        }

        if (!authenticated)
        {
            return new ClientAuthResult(false, client, "unauthorized_client");
        }

        return new ClientAuthResult(true, client);
    }

    private bool ShouldCheckMtls(ClientCredentialInput input, Client client)
    {
        if (input.Usage == ClientAuthenticationUsage.TokenEndpoint && 
            string.Equals(input.GrantType, OAuthConstants.GrantTypes.ClientCredentials, StringComparison.Ordinal))
        {
            return true;
        }
        if (input.Usage == ClientAuthenticationUsage.Introspection)
        {
            return true;
        }
        return false;
    }

    private string[]? GetAllowedMtlsThumbprints(ClientCredentialInput input, Client client)
    {
        if (input.Usage == ClientAuthenticationUsage.TokenEndpoint)
        {
            if (!string.IsNullOrWhiteSpace(client.M2MMtlsThumbprintsJson))
            {
                try { return JsonSerializer.Deserialize<string[]>(client.M2MMtlsThumbprintsJson); }
                catch { return null; }
            }
        }
        else if (input.Usage == ClientAuthenticationUsage.Introspection)
        {
             // Check client-specific
            if (!string.IsNullOrEmpty(client.IntrospectionMtlsThumbprintsJson))
            {
                try { return JsonSerializer.Deserialize<string[]>(client.IntrospectionMtlsThumbprintsJson); }
                catch { }
            }
            // Check global
            if (authOptions.Value.IntrospectionMtlsCertificates is { Count: > 0 })
            {
                if (authOptions.Value.IntrospectionMtlsCertificates.TryGetValue(input.ClientId, out var thumbprints))
                {
                    return thumbprints;
                }
            }
        }
        return null;
    }
}
