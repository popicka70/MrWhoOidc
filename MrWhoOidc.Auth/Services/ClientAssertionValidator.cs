using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Collections.Concurrent;

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

public sealed class ClientAssertionValidator(
    AuthDbContext db,
    IHttpClientFactory? httpClientFactory = null,
    IJwksCache? jwksCache = null,
    IOptions<AuthOptions>? authOptions = null) : IClientAssertionValidator
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ReplayStore = new(StringComparer.Ordinal);

    public async Task<bool> ValidateAsync(string clientId, string assertion, string tokenEndpoint, CancellationToken ct = default)
    {
        // Ensure client exists
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        if (client == null) return false;

        var signingKeys = await ClientJwksResolver.GetSigningKeysAsync(
            client,
            httpClientFactory,
            jwksCache,
            authOptions?.Value.ClientJwksCacheSeconds ?? 300,
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

        // Validate signature, audience, lifetime
        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = clientId,
            ValidateAudience = true,
            // Accept either the absolute token endpoint URL or issuer base + "/token"
            ValidAudiences = new[] { tokenEndpoint },
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
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

            var expiresAt = jwt.Payload.Expiration.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(jwt.Payload.Expiration.Value).Add(tvp.ClockSkew)
                : DateTimeOffset.UtcNow.Add(tvp.ClockSkew);
            if (!TryAddReplayEntry($"client-assertion:{clientId}:{tokenEndpoint}:{jti}", expiresAt))
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

    private static bool TryAddReplayEntry(string key, DateTimeOffset expiresAt)
    {
        CleanupReplayEntries();
        if (ReplayStore.TryAdd(key, expiresAt))
        {
            return true;
        }

        if (ReplayStore.TryGetValue(key, out var existing) && existing <= DateTimeOffset.UtcNow)
        {
            ReplayStore.TryRemove(key, out _);
            return ReplayStore.TryAdd(key, expiresAt);
        }

        return false;
    }

    private static void CleanupReplayEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ReplayStore)
        {
            if (entry.Value <= now)
            {
                ReplayStore.TryRemove(entry.Key, out _);
            }
        }
    }
}
