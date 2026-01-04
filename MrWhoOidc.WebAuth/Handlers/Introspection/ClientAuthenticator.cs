using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Authenticates clients for introspection requests using mTLS, private_key_jwt, or client_secret.
/// </summary>
public sealed class ClientAuthenticator(
    IClientStore clientStore,
    IClientAssertionValidator assertionValidator,
    IOptions<AuthOptions> authOptions,
    IMtlsThumbprintResolver mtlsThumbprintResolver,
    ILogger<ClientAuthenticator> logger)
{
    public async Task<(bool Authenticated, IResult? ErrorResult)> AuthenticateAsync(IntrospectionContext context)
    {
        var client = context.Client;
        var request = context.Request;
        var http = context.HttpContext;

        // Try mTLS authentication first
        var mtlsThumbprints = GetAllowedMtlsThumbprints(client, request.ClientId);
        if (mtlsThumbprints is { Length: > 0 })
        {
            return AuthenticateViaMtls(http, mtlsThumbprints, context.ClientBucket);
        }

        // Try private_key_jwt authentication
        if (IsPrivateKeyJwtRequest(request))
        {
            var authenticated = await assertionValidator.ValidateAsync(
                request.ClientId,
                request.ClientAssertion!,
                context.Endpoint
            ).ConfigureAwait(false);

            if (!authenticated)
            {
                return (false, Results.BadRequest(new { error = "unauthorized_client" }));
            }

            return (true, null);
        }

        // Fall back to client_secret authentication
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
        if (string.IsNullOrEmpty(client.ClientSecretHash))
        {
            return (false, Results.BadRequest(new { error = "unauthorized_client" }));
        }
#pragma warning restore CS0618

        var secretValid = await clientStore.ValidateClientSecretAsync(request.ClientId, request.ClientSecret).ConfigureAwait(false);
        if (!secretValid)
        {
            return (false, Results.BadRequest(new { error = "unauthorized_client" }));
        }

        return (true, null);
    }

    private string[]? GetAllowedMtlsThumbprints(Client client, string clientId)
    {
        // Check client-specific DB configuration first
        if (!string.IsNullOrEmpty(client.IntrospectionMtlsThumbprintsJson))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(client.IntrospectionMtlsThumbprintsJson);
            }
            catch
            {
                // Fall through to global config
            }
        }

        // Check global configuration
        if (authOptions.Value.IntrospectionMtlsCertificates is { Count: > 0 })
        {
            if (authOptions.Value.IntrospectionMtlsCertificates.TryGetValue(clientId, out var thumbprints))
            {
                return thumbprints;
            }
        }

        return null;
    }

    private (bool Authenticated, IResult? ErrorResult) AuthenticateViaMtls(
        HttpContext http,
        string[] allowedThumbprints,
        string clientBucket)
    {
        var cert = http.Connection.ClientCertificate;
        if (cert is null)
        {
            logger.LogWarning("Introspection mTLS: no client certificate provided for client {ClientBucket}", clientBucket);
            return (false, Results.BadRequest(new { error = "unauthorized_client" }));
        }

        var presentedX5tS256 = mtlsThumbprintResolver.ResolveThumbprint(cert);
        var presentedHex = cert.GetCertHashString(HashAlgorithmName.SHA256);

        static bool HasValue(string? v) => !string.IsNullOrWhiteSpace(v);

        var match =
            (HasValue(presentedX5tS256) && allowedThumbprints.Any(t => string.Equals(t, presentedX5tS256, StringComparison.OrdinalIgnoreCase))) ||
            (HasValue(presentedHex) && allowedThumbprints.Any(t => string.Equals(t, presentedHex, StringComparison.OrdinalIgnoreCase)));

        if (!match)
        {
            logger.LogWarning("Introspection mTLS: certificate thumbprint mismatch for client {ClientBucket}", clientBucket);
            return (false, Results.BadRequest(new { error = "unauthorized_client" }));
        }

        return (true, null);
    }

    private static bool IsPrivateKeyJwtRequest(IntrospectionRequest request)
    {
        return string.Equals(
            request.ClientAssertionType,
            "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            StringComparison.Ordinal
        ) && !string.IsNullOrEmpty(request.ClientAssertion);
    }
}
