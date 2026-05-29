using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for generating JWT Secured Authorization Responses (JARM).
/// </summary>
public interface IJarmService
{
    /// <summary>
    /// Creates a successful JARM response.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="code">The authorization code.</param>
    /// <param name="responseMode">The response mode.</param>
    /// <param name="state">The state parameter.</param>
    /// <returns>A signed (and optionally encrypted) JWT response.</returns>
    Task<string> CreateSuccessResponseAsync(string clientId, string issuer, string code, string responseMode, string? state);

    /// <summary>
    /// Creates an error JARM response.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="error">The error code.</param>
    /// <param name="errorDescription">The error description.</param>
    /// <param name="state">The state parameter.</param>
    /// <returns>A signed (and optionally encrypted) JWT response.</returns>
    Task<string> CreateErrorResponseAsync(string clientId, string issuer, string error, string errorDescription, string? state);
}

public class JarmService : IJarmService
{
    private readonly IClientStore _clients;
    private readonly IJwtService _jwt;
    private readonly ICachedKeyProvider _keyProvider;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IJwksCache? _jwksCache;
    private readonly IOptions<AuthOptions>? _authOptions;
    private readonly IClientJwksProvider _clientJwksProvider;

    public JarmService(
        IClientStore clients,
        IJwtService jwt,
        ICachedKeyProvider keyProvider,
        IHttpClientFactory? httpClientFactory = null,
        IJwksCache? jwksCache = null,
        IOptions<AuthOptions>? authOptions = null,
        IClientJwksProvider? clientJwksProvider = null)
    {
        _clients = clients;
        _jwt = jwt;
        _keyProvider = keyProvider;
        _httpClientFactory = httpClientFactory;
        _jwksCache = jwksCache;
        _authOptions = authOptions;
        _clientJwksProvider = clientJwksProvider ?? new ClientJwksResolver();
    }

    public async Task<string> CreateSuccessResponseAsync(string clientId, string issuer, string code, string responseMode, string? state)
    {
        var enc = await TryGetEncryptingCredentialsAsync(clientId);

        var activeKey = await _keyProvider.GetActiveSigningKeyAsync().ConfigureAwait(false);
        var signingAlg = activeKey is JsonWebKey jwk && !string.IsNullOrWhiteSpace(jwk.Alg) ? jwk.Alg : SecurityConstants.JwtAlgorithms.RS256;

        var claims = new List<Claim>
        {
            new(OAuthConstants.Parameters.Code, code)
        };

        // c_hash per JARM
        var cHash = CryptoHelper.ComputeLeftHalfHashBase64Url(code, signingAlg);
        claims.Add(new(OidcConstants.Claims.CHash, cHash));

        if (!string.IsNullOrEmpty(state))
        {
            claims.Add(new(OAuthConstants.Parameters.State, state));
            var sHash = CryptoHelper.ComputeLeftHalfHashBase64Url(state, signingAlg);
            claims.Add(new(OidcConstants.Claims.SHash, sHash));
        }

        var exp = DateTimeOffset.UtcNow.AddMinutes(5);

        if (enc is not null)
        {
            return await _jwt.CreateJwtEncryptedAsync(issuer, clientId, claims, exp, enc).ConfigureAwait(false);
        }

        return await _jwt.CreateJwtAsync(issuer, clientId, claims, exp).ConfigureAwait(false);
    }

    public async Task<string> CreateErrorResponseAsync(string clientId, string issuer, string error, string errorDescription, string? state)
    {
        var enc = await TryGetEncryptingCredentialsAsync(clientId);

        var activeKey = await _keyProvider.GetActiveSigningKeyAsync().ConfigureAwait(false);
        var signingAlg = activeKey is JsonWebKey jwk && !string.IsNullOrWhiteSpace(jwk.Alg) ? jwk.Alg : SecurityConstants.JwtAlgorithms.RS256;

        var claims = new List<Claim>
        {
            new(OAuthConstants.Parameters.Error, error),
            new(OAuthConstants.Parameters.ErrorDescription, errorDescription)
        };

        if (!string.IsNullOrEmpty(state))
        {
            claims.Add(new(OAuthConstants.Parameters.State, state));
            var sHash = CryptoHelper.ComputeLeftHalfHashBase64Url(state, signingAlg);
            claims.Add(new(OidcConstants.Claims.SHash, sHash));
        }

        var exp = DateTimeOffset.UtcNow.AddMinutes(5);

        if (enc is not null)
        {
            return await _jwt.CreateJwtEncryptedAsync(issuer, clientId, claims, exp, enc).ConfigureAwait(false);
        }

        return await _jwt.CreateJwtAsync(issuer, clientId, claims, exp).ConfigureAwait(false);
    }

    private async Task<EncryptingCredentials?> TryGetEncryptingCredentialsAsync(string? clientId)
    {
        try
        {
            if (string.IsNullOrEmpty(clientId)) return null;
            var client = await _clients.FindByClientIdAsync(clientId);
            if (client is null) return null;

            // Only encrypt JARM when the client explicitly opts in via client metadata.
            if (string.IsNullOrWhiteSpace(client.AuthorizationEncryptedResponseAlg) || string.IsNullOrWhiteSpace(client.AuthorizationEncryptedResponseEnc))
            {
                return null;
            }

            if (!string.Equals(client.AuthorizationEncryptedResponseAlg, SecurityAlgorithms.RsaOAEP, StringComparison.Ordinal)
                || !string.Equals(client.AuthorizationEncryptedResponseEnc, SecurityAlgorithms.Aes256CbcHmacSha512, StringComparison.Ordinal))
            {
                // Enforce supported alg/enc pair. Discovery advertises what's supported.
                return null;
            }

            var key = await _clientJwksProvider.GetEncryptionKeyAsync(
                client,
                _httpClientFactory,
                _jwksCache,
                _authOptions?.Value.ClientJwksCacheSeconds ?? 300).ConfigureAwait(false);

            if (key is null) return null;

            var encCreds = new EncryptingCredentials(key, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512);
            return encCreds;
        }
        catch
        {
            return null;
        }
    }
}
