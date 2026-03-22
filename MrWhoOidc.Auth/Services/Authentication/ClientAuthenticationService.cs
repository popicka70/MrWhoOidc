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
    /// <summary>
    /// Authenticates a client based on the provided credentials.
    /// </summary>
    /// <param name="input">The client credentials input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result indicating success or failure and the authenticated client.</returns>
    public async Task<ClientAuthResult> AuthenticateAsync(ClientCredentialInput input, CancellationToken ct = default)
    {
        // 1. Load Client
        var client = await clientStore.FindByClientIdAsync(input.ClientId, ct).ConfigureAwait(false);
        if (client is null)
        {
            logger.LogWarning("Client authentication failed: Unknown client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
            return new ClientAuthResult(false, null, "unauthorized_client", "Unknown client");
        }

        // 2. mTLS Checks / Authentication (RFC 8705)
        // Notes:
        // - Token endpoint support is driven by client.M2MMtlsThumbprintsJson.
        // - When configured and the presented certificate matches, mTLS can be used as the
        //   client authentication method for token endpoint requests.
        bool mtlsConfigured = false;
        bool mtlsMatched = false;
        if (ShouldCheckMtls(input, client))
        {
            var allowedThumbprints = GetAllowedMtlsThumbprints(input, client);
            if (allowedThumbprints is { Length: > 0 })
            {
                mtlsConfigured = true;
                var presented = input.MtlsThumbprint;
                var presentedHex = input.MtlsThumbprintHexSha256;

                static bool HasValue(string? v) => !string.IsNullOrWhiteSpace(v);

                var ok =
                    (HasValue(presented) && allowedThumbprints.Any(a => string.Equals(a, presented, StringComparison.OrdinalIgnoreCase))) ||
                    (HasValue(presentedHex) && allowedThumbprints.Any(a => string.Equals(a, presentedHex, StringComparison.OrdinalIgnoreCase)));
                if (!ok)
                {
                    logger.LogWarning("Client authentication failed: mTLS required but missing/invalid for client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                    return new ClientAuthResult(false, client, "invalid_client", "mtls_required");
                }

                mtlsMatched = true;
            }
        }

        // If mTLS is configured and matched, accept it as the authentication method.
        // (If the caller also provided a client_assertion, we still validate the assertion below.)
        if (mtlsConfigured && mtlsMatched &&
            input.Usage == ClientAuthenticationUsage.TokenEndpoint &&
            (string.IsNullOrEmpty(input.ClientAssertionType) || string.IsNullOrEmpty(input.ClientAssertion)))
        {
            return new ClientAuthResult(true, client);
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
            // Check policies if Client Credentials Grant — a client_secret must be provided
            // because public clients are not allowed to use client_credentials (RFC 6749 §4.4).
            if (input.Usage == ClientAuthenticationUsage.TokenEndpoint &&
                string.Equals(input.GrantType, OAuthConstants.GrantTypes.ClientCredentials, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(input.ClientSecret))
                {
                    logger.LogWarning("Client authentication failed: client_secret required for client_credentials {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                    return new ClientAuthResult(false, client, "unauthorized_client");
                }
            }

            // Token exchange requires a confidential client (RFC 8693 §2.1).
            if (input.Usage == ClientAuthenticationUsage.TokenEndpoint &&
                string.Equals(input.GrantType, OAuthConstants.GrantTypes.TokenExchange, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(input.ClientSecret))
                {
                    logger.LogWarning("Client authentication failed: client_secret required for token-exchange {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                    return new ClientAuthResult(false, client, "unauthorized_client");
                }
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
        if (input.Usage == ClientAuthenticationUsage.TokenEndpoint)
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
                catch (JsonException ex)
                {
                    logger.LogDebug(ex, "Failed to parse M2M mTLS thumbprints for client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                    return null;
                }
            }
        }
        else if (input.Usage == ClientAuthenticationUsage.Introspection)
        {
            // Check client-specific
            if (!string.IsNullOrEmpty(client.IntrospectionMtlsThumbprintsJson))
            {
                try { return JsonSerializer.Deserialize<string[]>(client.IntrospectionMtlsThumbprintsJson); }
                catch (JsonException ex)
                {
                    logger.LogDebug(ex, "Failed to parse introspection mTLS thumbprints for client {ClientIdHash}", Bucketization.Bucket(input.ClientId));
                }
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
