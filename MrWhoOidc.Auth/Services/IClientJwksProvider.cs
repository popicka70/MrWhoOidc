using System.Net.Http;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Provides methods to resolve JWKS for clients.
/// </summary>
public interface IClientJwksProvider
{
    /// <summary>
    /// Gets signing keys for a client.
    /// </summary>
    /// <param name="client">The client to get keys for.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory.</param>
    /// <param name="jwksCache">Optional JWKS cache.</param>
    /// <param name="cacheSeconds">Cache duration in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of security keys for signing.</returns>
    Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(
        Client client,
        IHttpClientFactory? httpClientFactory = null,
        IJwksCache? jwksCache = null,
        int cacheSeconds = 300,
        CancellationToken ct = default);

    /// <summary>
    /// Gets encryption key for a client.
    /// </summary>
    /// <param name="client">The client to get encryption key for.</param>
    /// <param name="httpClientFactory">Optional HTTP client factory.</param>
    /// <param name="jwksCache">Optional JWKS cache.</param>
    /// <param name="cacheSeconds">Cache duration in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON web key for encryption, or null if not found.</returns>
    Task<JsonWebKey?> GetEncryptionKeyAsync(
        Client client,
        IHttpClientFactory? httpClientFactory = null,
        IJwksCache? jwksCache = null,
        int cacheSeconds = 300,
        CancellationToken ct = default);

    /// <summary>
    /// Parses security keys from JWK or JWKS JSON.
    /// </summary>
    /// <param name="jwkOrJwksJson">The JWK or JWKS JSON string.</param>
    /// <returns>Collection of security keys.</returns>
    IReadOnlyCollection<SecurityKey> ParseSecurityKeys(string? jwkOrJwksJson);
}