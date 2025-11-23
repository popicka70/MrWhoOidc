using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

public interface ITokenExchangeService
{
    Task<(bool ok, object? payload, string? error, int status)> ExchangeTokenAsync(
        string subjectToken,
        string? subjectTokenType,
        string? requestedTokenType,
        string? requestedAudience,
        string[] requestedScopes,
        string callerClientId,
        string issuer,
        string? dpopJkt,
        CancellationToken ct = default);
}

public class TokenExchangeService(
    AuthDbContext db,
    IJwtService jwt,
    IOptions<AuthOptions> authOptions,
    ITokenValidator validator,
    ITenantSettingsService settingsService,
    IScopeResolver scopeResolver,
    IOboPolicyService? oboPolicy = null) : ITokenExchangeService
{
    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeTokenAsync(
        string subjectToken,
        string? subjectTokenType,
        string? requestedTokenType,
        string? requestedAudience,
        string[] requestedScopes,
        string callerClientId,
        string issuer,
        string? dpopJkt,
        CancellationToken ct = default)
    {
        // Load tenant settings for token lifetime
        var settings = await settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
        var accessTokenLifetime = TimeSpan.FromSeconds(settings.Tokens?.AccessTokenLifetimeSeconds ?? 3600);

        // Support only access tokens for MVP
        if (!string.IsNullOrEmpty(requestedTokenType) && !string.Equals(requestedTokenType, "urn:ietf:params:oauth:token-type:access_token", StringComparison.Ordinal))
        {
            return (false, new { error = "unsupported_token_type" }, "unsupported_token_type", 400);
        }

        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            return (false, new { error = "invalid_request", error_description = "missing subject_token" }, "invalid_request", 400);
        }

        // Determine subject token type
        // Treat as JWT only if it looks like one (3 segments) or the subject_token_type explicitly says JWT
        var isLikelyJwt = subjectToken.Count(c => c == '.') == 2;
        var isJwt = isLikelyJwt || string.Equals(subjectTokenType, "urn:ietf:params:oauth:token-type:jwt", StringComparison.Ordinal);

        Guid userId;
        string[] subjectScopes = Array.Empty<string>();
        string? sourceAudience = null;
        string? subjectCnfJkt = null;
        string? subjectTenantId = null;
        DateTimeOffset subjectExpiry;
        int subjectDelegationDepth = 0; // for opaque subjects

        if (isJwt)
        {
            // Validate as local JWT access token
            var (ok, principal, error) = validator.Validate(subjectToken, issuer);
            if (!ok || principal is null)
            {
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }

            var sub = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out userId))
            {
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }

            // Capture tenant_id claim if present
            subjectTenantId = principal.FindFirst("tenant_id")?.Value;

            // scope claim is space-delimited
            var scopeStr = principal.FindFirst("scope")?.Value;
            subjectScopes = string.IsNullOrWhiteSpace(scopeStr) ? Array.Empty<string>() : scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // aud from JWT (not validated by validator); parse token directly
            try
            {
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var unsigned = handler.ReadJwtToken(subjectToken);
                var expUnix = unsigned.Payload.Expiration;
                subjectExpiry = expUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(expUnix.Value) : DateTimeOffset.UtcNow.AddMinutes(15);
                if (unsigned.Audiences is not null) sourceAudience = unsigned.Audiences.FirstOrDefault();
                // If server defines allowed ApiAudiences and token has aud, ensure aud is one of them
                var allowedAudiences = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
                if (!string.IsNullOrEmpty(sourceAudience) && allowedAudiences.Length > 0 && !allowedAudiences.Contains(sourceAudience, StringComparer.Ordinal))
                {
                    return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
                }
                // Single-hop: reject if act present
                if (unsigned.Payload.TryGetValue("act", out _))
                {
                    return (false, new { error = "invalid_grant", error_description = "single_hop_only" }, "invalid_grant", 400);
                }
                if (unsigned.Payload.TryGetValue("cnf", out var cnfVal) && cnfVal is not null)
                {
                    try
                    {
                        // cnf claim stored as object or stringified json; handle both
                        string json = cnfVal is string s ? s : System.Text.Json.JsonSerializer.Serialize(cnfVal);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("jkt", out var jktEl)) subjectCnfJkt = jktEl.GetString();
                    }
                    catch { }
                }
            }
            catch
            {
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }
        }
        else
        {
            // Opaque access token: lookup in DB
            var hash = CryptoHelper.ComputeSha256Base64(subjectToken);
            var entity = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(t => t.Type == "access" && t.TokenHash == hash, ct).ConfigureAwait(false);
            if (entity is null || entity.RevokedAt is not null || entity.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }

            userId = entity.UserId;
            subjectExpiry = entity.ExpiresAt;
            sourceAudience = entity.Audience;
            subjectCnfJkt = entity.CnfJkt;
            try { subjectScopes = System.Text.Json.JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>(); }
            catch { subjectScopes = Array.Empty<string>(); }
            // Track delegation depth for opaque tokens
            subjectDelegationDepth = entity.DelegationDepth;
        }

        // Enforce DPoP bridging mode per-client policy
        // Defaults: Deny bridging; max delegation depth = 1 (single hop)
        var callerClient = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == callerClientId, ct).ConfigureAwait(false);
        var dpopMode = callerClient?.OboDpopMode ?? OboDpopMode.Deny;
        var maxDepth = callerClient?.OboMaxDelegationDepth ?? 1;

        // Delegation depth enforcement for opaque subjects
        if (!isJwt)
        {
            var newDepth = subjectDelegationDepth + 1;
            if (newDepth > maxDepth)
            {
                return (false, new { error = "invalid_grant", error_description = "max_delegation_depth_exceeded" }, "invalid_grant", 400);
            }
        }

        // DPoP bridging logic
        string? outCnfJkt = null;
        if (!string.IsNullOrEmpty(subjectCnfJkt))
        {
            switch (dpopMode)
            {
                case OboDpopMode.Deny:
                    return (false, new { error = "invalid_request", error_description = "dpop_bridging_not_supported" }, "invalid_request", 400);
                case OboDpopMode.RequireSameJkt:
                    if (string.IsNullOrEmpty(dpopJkt) || !string.Equals(dpopJkt, subjectCnfJkt, StringComparison.Ordinal))
                        return (false, new { error = "invalid_request", error_description = "dpop_same_key_required" }, "invalid_request", 400);
                    outCnfJkt = subjectCnfJkt; // bind outgoing to same key
                    break;
                case OboDpopMode.AllowSameJktOnly:
                    if (string.IsNullOrEmpty(dpopJkt) || !string.Equals(dpopJkt, subjectCnfJkt, StringComparison.Ordinal))
                        return (false, new { error = "invalid_request", error_description = "dpop_same_key_required" }, "invalid_request", 400);
                    outCnfJkt = subjectCnfJkt; // bind outgoing to same key
                    break;
            }
        }
        else
        {
            if (dpopMode == OboDpopMode.AllowSameJktOnly)
            {
                // Subject not DPoP-bound but policy requires same-jkt exchanges only
                return (false, new { error = "invalid_request", error_description = "dpop_same_key_required" }, "invalid_request", 400);
            }
            // For RequireSameJkt: no requirement when subject isn't bound; do not bind outgoing
        }

        // Resolve target audience
        string audience = requestedAudience ?? string.Empty;
        if (string.IsNullOrWhiteSpace(audience))
        {
            audience = (authOptions.Value.ApiAudiences?.FirstOrDefault()) ?? "api";
        }
        else
        {
            // Basic allow-list: audience must be in configured ApiAudiences if configured
            var allowed = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
            if (allowed.Length > 0 && !allowed.Contains(audience, StringComparer.Ordinal))
            {
                return (false, new { error = "invalid_target" }, "invalid_target", 400);
            }
        }
        // Evaluate via policy service if available
        string[] resultScopes;
        TimeSpan lifetime;
        if (oboPolicy is not null)
        {
            var eval = await oboPolicy.EvaluateAsync(callerClientId, sourceAudience, audience, subjectScopes, requestedScopes, subjectExpiry, ct).ConfigureAwait(false);
            if (!eval.ok)
            {
                return (false, new { error = eval.error ?? "invalid_request" }, eval.error, eval.status);
            }
            resultScopes = eval.scopes;
            lifetime = eval.lifetime;
        }
        else
        {
            // Fallback MVP behavior
            HashSet<string> resultScopesSet = new(StringComparer.Ordinal);
            if (requestedScopes is { Length: > 0 })
            {
                var subjectSet = new HashSet<string>(subjectScopes, StringComparer.Ordinal);
                foreach (var s in requestedScopes)
                {
                    if (subjectSet.Contains(s)) resultScopesSet.Add(s);
                }
            }
            else
            {
                foreach (var s in subjectScopes) resultScopesSet.Add(s);
            }
            resultScopes = resultScopesSet.ToArray();
            if (resultScopes.Length == 0)
            {
                return (false, new { error = "insufficient_scope" }, "insufficient_scope", 400);
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var remaining = subjectExpiry - nowUtc;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var policyMax = accessTokenLifetime; // Use tenant setting
            lifetime = remaining <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : (remaining < policyMax ? remaining : policyMax);
        }

        // Issue token: JWT or opaque per config
        var opaqueEnabled = authOptions.Value.OpaqueAccessTokens?.Enabled == true &&
            (authOptions.Value.OpaqueAccessTokens.Audiences is null || authOptions.Value.OpaqueAccessTokens.Audiences.Length == 0 ||
             authOptions.Value.OpaqueAccessTokens.Audiences.Contains(audience, StringComparer.Ordinal));

        var jtiNew = Guid.NewGuid().ToString("N");
        string accessToken;
        if (opaqueEnabled)
        {
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var actObj = new { sub = callerClientId };
            var actJson = System.Text.Json.JsonSerializer.Serialize(actObj);
            // Compute new delegation depth for opaque subjects (JWT subjects will start at 1)
            var newDepth = isJwt ? 1 : subjectDelegationDepth + 1;
            await PersistOpaqueAccessAsync(userId, callerClientId, audience, resultScopes, jtiNew, raw, lifetime, cnfJkt: outCnfJkt, ct, actJson: actJson, delegationDepth: newDepth).ConfigureAwait(false);
            accessToken = raw;
        }
        else
        {
            var claims = new List<System.Security.Claims.Claim>
            {
                new("sub", userId.ToString()),
                new("jti", jtiNew),
                new("scope", string.Join(' ', resultScopes)),
                new("act", System.Text.Json.JsonSerializer.Serialize(new { sub = callerClientId }))
            };
            
            // Add tenant_id claim if any custom (non-standard) scopes are granted and tenant_id was in subject token
            var hasCustomScopes = resultScopes.Any(s => !scopeResolver.IsStandardScope(s));
            if (hasCustomScopes && !string.IsNullOrEmpty(subjectTenantId))
            {
                claims.Add(new System.Security.Claims.Claim("tenant_id", subjectTenantId));
            }
            
            if (!string.IsNullOrEmpty(outCnfJkt))
            {
                var cnf = System.Text.Json.JsonSerializer.Serialize(new { jkt = outCnfJkt });
                claims.Add(new("cnf", cnf));
            }
            var nowUtc = DateTimeOffset.UtcNow;
            accessToken = jwt.CreateJwt(issuer, audience, claims, nowUtc.Add(lifetime));
        }

        var payload = new
        {
            access_token = accessToken,
            issued_token_type = "urn:ietf:params:oauth:token-type:access_token",
            token_type = "Bearer",
            expires_in = (int)lifetime.TotalSeconds,
            scope = string.Join(' ', resultScopes)
        };
        return (true, payload, null, 200);
    }

    async Task PersistOpaqueAccessAsync(Guid userId, string clientId, string audience, string[] scopes, string jti, string rawToken, TimeSpan lifetime, string? cnfJkt, CancellationToken ct, string? actJson = null, int delegationDepth = 0)
    {
        var hash = CryptoHelper.ComputeSha256Base64(rawToken);
        var entity = new Persistence.Token
        {
            Type = "access",
            TokenHash = hash,
            UserId = userId,
            ClientId = clientId,
            ScopesJson = JsonSerializer.Serialize(scopes),
            Audience = audience,
            Jti = jti,
            CnfJkt = cnfJkt,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
            ActJson = actJson,
            DelegationDepth = delegationDepth
        };
        db.Tokens.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
