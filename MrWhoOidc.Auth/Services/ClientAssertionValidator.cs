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
}

public sealed class ClientAssertionValidator : IClientAssertionValidator
{
    /// <summary>The furthest ahead an assertion's exp may be. RFC 7523 leaves it open; a few minutes is customary.</summary>
    internal static readonly TimeSpan MaxAssertionLifetime = TimeSpan.FromMinutes(10);

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

    public async Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default)
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
            // Accept either the absolute token endpoint URL or issuer base + "/token"
            ValidAudiences = new[] { tokenEndpoint },
            ValidateLifetime = true,
            ClockSkew = clockSkew,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            // Restrict to common signature algs used for client assertions
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                                       SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512 }
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(assertion, tvp, out _);

            // An assertion is single-use and short-lived. Without a cap, one with exp years ahead stayed replayable
            // wherever the replay cache does not reach (another pod with the in-memory fallback, a restart).
            var now = DateTimeOffset.UtcNow;
            if (jwt.Payload.Expiration is not { } exp
                || DateTimeOffset.FromUnixTimeSeconds(exp) > now.Add(MaxAssertionLifetime).Add(tvp.ClockSkew))
            {
                return false;
            }
            if (jwt.Payload.IssuedAt is { } iat && iat > now.Add(tvp.ClockSkew).UtcDateTime)
            {
                return false;
            }

            var expiresAt = jwt.Payload.Expiration.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(jwt.Payload.Expiration.Value).Add(tvp.ClockSkew)
                : DateTimeOffset.UtcNow.Add(tvp.ClockSkew);
            if (!_replayCache.TryAdd($"client-assertion:{clientId}:{tokenEndpoint}:{jti}", expiresAt))
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
