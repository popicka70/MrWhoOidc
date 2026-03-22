using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of IClientCredentialsTokenFactory that extracts logic from TokenService.
/// </summary>
public sealed class ClientCredentialsTokenFactory(
    AuthDbContext db,
    IJwtService jwt,
    IOptions<AuthOptions> authOptions,
    ITenantSettingsService settingsService,
    IScopeResolver scopeResolver,
    ITokenLifetimeResolver lifetimeResolver,
    ILogger<ClientCredentialsTokenFactory> logger) : IClientCredentialsTokenFactory
{
    public async Task<(bool ok, object? payload, string? error, int status)> CreateTokenAsync(ClientCredentialsRequest request, CancellationToken ct = default)
    {
        // Fail-closed for product scopes on client_credentials.
        if (request.RequestedScopes.Any(ProductScopeClassifier.IsProductScope))
        {
            return (false, new { error = OAuthConstants.ErrorCodes.InvalidScope, error_description = "product scopes are not supported for client_credentials" }, OAuthConstants.ErrorCodes.InvalidScope, 400);
        }

        var settings = await settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == request.ClientId, ct).ConfigureAwait(false);
        if (client is null)
        {
            return (false, new { error = "unauthorized_client" }, "unauthorized_client", 400);
        }

        if (!client.AllowClientCredentials)
        {
            return (false, new { error = OAuthConstants.ErrorCodes.UnauthorizedClient, error_description = "Client is not authorized for client_credentials grant" }, OAuthConstants.ErrorCodes.UnauthorizedClient, 400);
        }

        string[] perClientAudiences = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(client.M2MAllowedAudiencesJson))
        {
            try { perClientAudiences = JsonSerializer.Deserialize<string[]>(client.M2MAllowedAudiencesJson) ?? Array.Empty<string>(); }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Failed to parse M2M allowed audiences for client {ClientId}", request.ClientId);
                perClientAudiences = Array.Empty<string>();
            }
        }
        var globalAudiences = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
        var allowedAudiences = perClientAudiences.Length > 0 ? perClientAudiences : globalAudiences;
        if (allowedAudiences.Length > 0 && !allowedAudiences.Contains(request.Audience, StringComparer.Ordinal))
        {
            return (false, new { error = "invalid_target", error_description = "audience not allowed" }, "invalid_target", 400);
        }

        var allowedScopeNames = await db.ClientScopes.AsNoTracking()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        var granted = new List<string>();
        if (request.RequestedScopes is { Length: > 0 })
        {
            foreach (var s in request.RequestedScopes)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (string.Equals(s, "openid", StringComparison.Ordinal) || string.Equals(s, "offline_access", StringComparison.Ordinal))
                    continue;
                if (allowedScopeNames.Contains(s, StringComparer.Ordinal))
                    granted.Add(s);
            }
        }

        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", request.ClientId),
            new("client_id", request.ClientId),
            new("jti", jti)
        };
        if (granted.Count > 0)
        {
            claims.Add(new("scope", string.Join(' ', granted)));

            var hasCustomScopes = granted.Any(s => !scopeResolver.IsStandardScope(s));
            if (hasCustomScopes && client.TenantId != Guid.Empty)
            {
                claims.Add(new("tenant_id", client.TenantId.ToString()));
            }
        }
        // Build cnf claim if DPoP or MTLS binding present
        var cnfDict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(request.DpopJkt)) cnfDict["jkt"] = request.DpopJkt;
        if (!string.IsNullOrEmpty(request.MtlsX5tS256)) cnfDict["x5t#S256"] = request.MtlsX5tS256;
        if (cnfDict.Count > 0)
        {
            var cnf = JsonSerializer.Serialize(cnfDict);
            claims.Add(new("cnf", cnf));
        }

        var realmName = await db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(realmName))
        {
            claims.Add(new("realm", realmName));
        }

        var lifetime = lifetimeResolver.ResolveAccessTokenLifetime(client, settings);

        var expiry = DateTimeOffset.UtcNow.Add(lifetime);
        var accessToken = await jwt.CreateJwtAsync(request.Issuer, request.Audience, claims, expiry, tokenType: SecurityConstants.JwtTokenTypes.AtJwt, ct: ct).ConfigureAwait(false);

        var payload = new
        {
            access_token = accessToken,
            token_type = !string.IsNullOrEmpty(request.DpopJkt) ? "DPoP" : "Bearer",
            expires_in = (int)lifetime.TotalSeconds,
            scope = granted.Count > 0 ? string.Join(' ', granted) : null
        };
        return (true, payload, null, 200);
    }
}
