using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for validating client assertions (private_key_jwt).
/// </summary>
public interface IClientAssertionValidator
{
    /// <summary>
    /// Validates a client assertion for a specific client and endpoint.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="assertion">The JWT assertion.</param>
    /// <param name="tokenEndpoint">The expected audience (token endpoint).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the assertion is valid; otherwise, false.</returns>
    Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default);

    /// <summary>
    /// Validates a client assertion whose <c>aud</c> may be any of <paramref name="validAudiences"/>,
    /// typically the endpoint URL and the issuer identifier (RFC 7523 §3, OIDC Core §9).
    /// </summary>
    Task<bool> ValidateAsync(string clientId, string assertion, IReadOnlyCollection<string> validAudiences, CancellationToken ct = default)
        => ValidateAsync(clientId, assertion, validAudiences.First(), ct);
}

public sealed class ClientAssertionValidator : IClientAssertionValidator
{
    /// <summary>
    /// JWS algorithms accepted for <c>private_key_jwt</c> client assertions; advertised in discovery
    /// as <c>*_endpoint_auth_signing_alg_values_supported</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedSigningAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
    ];

    private readonly AuthDbContext _db;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IJwksCache? _jwksCache;
    private readonly IOptions<AuthOptions>? _authOptions;
    private readonly IClientJwksProvider _clientJwksProvider;
    private readonly IJarReplayCache _replayCache;
    // Fallback used only when no replay cache is supplied (e.g. direct construction in tests).
    // Production resolves a distributed (Redis-backed) IJarReplayCache via DI so that
    // assertion replay protection spans all instances.
    private static readonly InMemoryJarReplayCache FallbackReplayCache = new();

    public ClientAssertionValidator(
        AuthDbContext db,
        IHttpClientFactory? httpClientFactory = null,
        IJwksCache? jwksCache = null,
        IOptions<AuthOptions>? authOptions = null,
        IClientJwksProvider? clientJwksProvider = null,
        IJarReplayCache? replayCache = null)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _jwksCache = jwksCache;
        _authOptions = authOptions;
        _clientJwksProvider = clientJwksProvider ?? new ClientJwksResolver();
        _replayCache = replayCache ?? FallbackReplayCache;
    }

    public Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default)
        => ValidateAsync(clientId, assertion, [tokenEndpoint], ct);

    public async Task<bool> ValidateAsync(string clientId, string assertion, IReadOnlyCollection<string> validAudiences, CancellationToken ct = default)
    {
        // Ensure client exists
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        if (client == null) return false;

        var signingKeys = await _clientJwksProvider.GetSigningKeysAsync(
            client,
            _httpClientFactory,
            _jwksCache,
            _authOptions?.Value.ClientJwksCacheSeconds ?? 300,
            ct).ConfigureAwait(false);

        if (signingKeys.Count == 0)
        {
            return false;
        }

        // Parse without validating to check custom constraints (iss/sub/jti)
        JwtSecurityToken jwt;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            jwt = handler.ReadJwtToken(assertion);
        }
        catch
        {
            return false;
        }

        // iss and sub must both equal client_id per RFC 7523
        var iss = jwt.Issuer;
        var sub = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")?.Value;
        if (!string.Equals(iss, clientId, StringComparison.Ordinal) || !string.Equals(sub, clientId, StringComparison.Ordinal))
            return false;

        // jti must be present (uniqueness not enforced here)
        var jti = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti)) return false;

        var clockSkew = TimeSpan.FromSeconds(_authOptions?.Value.ClientAssertionClockSkewSeconds ?? 60);

        // Validate signature, audience, lifetime
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = clientId,
            ValidateAudience = true,
            // The endpoint URL and, when supplied by the caller, the issuer identifier
            ValidAudiences = validAudiences.Where(a => !string.IsNullOrEmpty(a)).ToArray(),
            ValidateLifetime = true,
            ClockSkew = clockSkew,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            // Restrict to common asymmetric signature algs used for client assertions
            ValidAlgorithms = SupportedSigningAlgorithms
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(assertion, tvp, out _);

            var expiresAt = jwt.Payload.Expiration.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(jwt.Payload.Expiration.Value).Add(tvp.ClockSkew)
                : DateTimeOffset.UtcNow.Add(tvp.ClockSkew);
            // Keyed per client + jti only: an assertion accepted with aud=issuer must not be replayable
            // at a different endpoint.
            if (!_replayCache.TryAdd($"client-assertion:{clientId}:{jti}", expiresAt))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
