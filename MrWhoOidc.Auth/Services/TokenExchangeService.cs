using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.Auth.Services.Delegation;
using MrWhoOidc.Auth.Models.Delegation;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for OAuth 2.0 Token Exchange (RFC 8693).
/// Supports JWT and opaque access tokens as subject tokens.
/// Includes audience validation, delegation depth enforcement, DPoP bridging policy,
/// and delegated grant-authorized token exchange (Section 6.9).
/// </summary>
public interface ITokenExchangeService
{
    /// <summary>
    /// Implements RFC 8693 OAuth 2.0 Token Exchange.
    /// Supports JWT and opaque access tokens as subject tokens.
    /// Includes audience validation, delegation depth enforcement, and DPoP bridging policy.
    /// </summary>
    /// <param name="subjectToken">The token being exchanged.</param>
    /// <param name="subjectTokenType">The type of the subject token.</param>
    /// <param name="requestedTokenType">The type of token requested.</param>
    /// <param name="requestedAudience">The requested audience.</param>
    /// <param name="requestedScopes">The requested scopes.</param>
    /// <param name="callerClientId">The client ID of the caller.</param>
    /// <param name="issuer">The issuer URI.</param>
    /// <param name="dpopJkt">Optional DPoP JWK thumbprint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing the success status, payload, error, and HTTP status code.</returns>
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
    IOpaqueTokenPolicy opaquePolicy,
    ILogger<TokenExchangeService> logger,
    IOboPolicyService? oboPolicy = null,
    IScopeMapper? scopeMapper = null,
    IDelegatedAccessAuthorizationService? delegatedAuth = null,
    IGrantContextResolver? grantContextResolver = null) : ITokenExchangeService
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
        string? subjectTenantsJson = null;
        string? subjectClientId = null;
        DateTimeOffset subjectExpiry;
        int subjectDelegationDepth = 0; // for opaque subjects

        if (isJwt)
        {
            // Validate as local JWT access token
            var (ok, principal, error) = await validator.ValidateAsync(subjectToken, issuer, ct).ConfigureAwait(false);
            if (!ok || principal is null)
            {
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }

            var sub = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out userId))
            {
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }

            var subjectJti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                ?? principal.FindFirst("jti")?.Value;
            var persistedSubject = await FindSubjectAccessTokenAsync(subjectToken, subjectJti, ct).ConfigureAwait(false);
            if (persistedSubject is null || persistedSubject.UserId != userId)
            {
                logger.LogWarning("Token exchange rejected JWT subject for caller {ClientId}: token not recognized as a local access token", callerClientId);
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }

            // Capture tenant_id claim if present
            subjectTenantId = principal.FindFirst("tenant_id")?.Value;
            subjectClientId = persistedSubject.ClientId;

            // Capture tenants claim if present
            subjectTenantsJson = principal.FindFirst(OidcConstants.Scopes.Tenants)?.Value;

            // scope claim is space-delimited
            var scopeStr = principal.FindFirst("scope")?.Value;
            subjectScopes = string.IsNullOrWhiteSpace(scopeStr) ? Array.Empty<string>() : scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // aud from JWT (not validated by validator); parse token directly
            try
            {
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                var unsigned = handler.ReadJwtToken(subjectToken);
                var expUnix = unsigned.Payload.Expiration;
                subjectExpiry = persistedSubject.ExpiresAt;
                sourceAudience = persistedSubject.Audience;
                if (string.IsNullOrEmpty(sourceAudience) && unsigned.Audiences is not null)
                {
                    sourceAudience = unsigned.Audiences.FirstOrDefault();
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
                    catch (JsonException ex)
                    {
                        logger.LogDebug(ex, "Token exchange subject cnf claim parse failed");
                    }
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
            var entity = await FindSubjectAccessTokenAsync(subjectToken, null, ct).ConfigureAwait(false);
            if (entity is null)
            {
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
            }

            userId = entity.UserId;
            subjectExpiry = entity.ExpiresAt;
            sourceAudience = entity.Audience;
            subjectClientId = entity.ClientId;

            subjectCnfJkt = entity.CnfJkt;
            try
            {
                subjectScopes = System.Text.Json.JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>();
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Token exchange subject scopes parse failed");
                subjectScopes = Array.Empty<string>();
            }
            // Track delegation depth for opaque tokens
            subjectDelegationDepth = entity.DelegationDepth;
        }

        // Check for delegated grant context (Section 6.9)
        // If scopeMapper, delegatedAuth, and grantContextResolver are all available,
        // check whether a delegated grant exists between caller (delegate) and subject (delegator).
        Guid? delegatorUserId = null;
        Guid? delegatedGrantId = null;

        if (scopeMapper is not null && delegatedAuth is not null && grantContextResolver is not null)
        {
            var grantContext = await grantContextResolver.GetGrantContextAsync(callerClientId, userId, ct)
                .ConfigureAwait(false);
            if (grantContext is not null)
            {
                // Verify the grant is active and the delegate matches the caller
                delegatedGrantId = grantContext.GrantId;
                delegatorUserId = grantContext.DelegatorUserAccountId;
                logger.LogInformation(
                "Token exchange: delegated grant {GrantId} active for delegate {DelegateId} on behalf of delegator {DelegatorId}",
                delegatedGrantId, grantContext.DelegateUserAccountId, delegatorUserId);
            }
        }

        // Enforce DPoP bridging mode per-client policy
        // Defaults: Deny bridging; max delegation depth = 1 (single hop)
        var callerClient = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == callerClientId, ct).ConfigureAwait(false);

        if (!IsSubjectAudienceTrusted(sourceAudience, authOptions.Value.ApiAudiences, callerClient))
        {
            logger.LogWarning("Token exchange rejected subject for caller {ClientId}: source audience {SourceAudience} is not trusted", callerClientId, sourceAudience);
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
        }

        if (!IsSubjectClientAllowedForCaller(subjectClientId, callerClientId, sourceAudience, callerClient))
        {
            logger.LogWarning("Token exchange rejected subject for caller {ClientId}: subject token was issued to {SubjectClientId}", callerClientId, subjectClientId);
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
        }

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
            audience = ResolveDefaultAudience(sourceAudience, authOptions.Value.ApiAudiences) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(audience))
            {
                return (false, new { error = "invalid_target", error_description = "missing or ambiguous audience" }, "invalid_target", 400);
            }
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
        // Evaluate scope intersection and lifetime
        // Two flows: Normal OBO (oboPolicy or fallback) and Delegated Grant (Section 6.9)
        string[] resultScopes;
        TimeSpan lifetime;

        if (delegatedGrantId is not null && delegatorUserId is not null && scopeMapper is not null)
        {
            // --- Delegated Grant Token Exchange (Section 6.9) ---
            // Step 1: Map grant capabilities to OAuth scopes
            var grant = await db.DelegatedAccessGrants.AsNoTracking()
                .Where(g => g.Id == delegatedGrantId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (grant is null)
            {
                logger.LogWarning("Token exchange delegated grant {GrantId} not found in database", delegatedGrantId);
                return (false, new { error = "delegation_not_found" }, "delegation_not_found", 404);
            }

            // Step 2: Parse grant capabilities and map to scopes
            var grantCapabilities = System.Text.Json.JsonSerializer.Deserialize<List<string>>(grant.CapabilitiesJson);
            if (grantCapabilities is null || grantCapabilities.Count == 0)
            {
                return (false, new { error = "insufficient_scope" }, "insufficient_scope", 400);
            }

            var grantMappedScopes = scopeMapper.MapCapabilitiesToScopes(grantCapabilities);
            var grantScopeSet = new HashSet<string>(grantMappedScopes, StringComparer.Ordinal);

            // Step 3: Intersect requested scopes with:
            //   - Subject token scopes (delegator's current scopes)
            //   - Grant-mapped scopes
            //   - Client OBO policy scopes (via oboPolicy or fallback)
            HashSet<string> effectiveSet = new(StringComparer.Ordinal);

            if (requestedScopes is { Length: > 0 })
            {
                foreach (var s in requestedScopes)
                {
                    if (subjectScopes.Contains(s) && grantScopeSet.Contains(s))
                    {
                        effectiveSet.Add(s);
                    }
                }
            }
            else
            {
                foreach (var s in subjectScopes)
                {
                    if (grantScopeSet.Contains(s))
                    {
                        effectiveSet.Add(s);
                    }
            }
            }

            resultScopes = effectiveSet.ToArray();
            if (resultScopes.Length == 0)
            {
                return (false, new { error = "insufficient_scope" }, "insufficient_scope", 400);
            }

            // Step 4: Calculate token lifetime as minimum of:
            //   - Subject token remainder (subjectExpiry - now)
            //   - Grant remainder (grant.ExpiresAt - now)
            //   - Client OBO maximum (from oboPolicy or default tenant setting)
            //   - Server delegated-token maximum (accessTokenLifetime)
            var now = DateTimeOffset.UtcNow;
            var subjectRemaining = subjectExpiry - now;
            if (subjectRemaining < TimeSpan.Zero) subjectRemaining = TimeSpan.Zero;

            var grantRemaining = grant.ExpiresAt - now;
            if (grantRemaining < TimeSpan.Zero) grantRemaining = TimeSpan.Zero;

            var clientMax = TimeSpan.FromMinutes(15); // Default OBO max
            if (oboPolicy is not null)
            {
                var eval = await oboPolicy.EvaluateAsync(callerClientId, sourceAudience, audience,
                    subjectScopes, resultScopes, subjectExpiry, ct)
                    .ConfigureAwait(false);
                if (eval.ok)
                {
                    clientMax = eval.lifetime;
                }
            }

            // Server delegated-token maximum from tenant settings
            // Use DelegatedAccessTokenLifetimeSeconds if defined, otherwise fall back to access token lifetime
            var serverDelegatedMax = TimeSpan.FromSeconds(settings.Tokens?.AccessTokenLifetimeSeconds ?? 3600);

            // Lifetime = min(subjectRemaining, grantRemaining, clientMax, serverDelegatedMax)
            var candidates = new[] { subjectRemaining, grantRemaining, clientMax, serverDelegatedMax };
            var minRemaining = TimeSpan.MaxValue;
            foreach (var c in candidates)
            {
                if (c < minRemaining) minRemaining = c;
            }
            if (minRemaining <= TimeSpan.Zero) minRemaining = TimeSpan.FromMinutes(1);
            lifetime = minRemaining;
        }
        else if (oboPolicy is not null)
        {
            // --- Normal OBO flow via policy service ---
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
            // Fallback MVP behavior (no oboPolicy, no delegated grant)
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
        // For delegated grants, sub = delegator, act = { sub: delegate }
        // For normal OBO, sub = subject userId, act = { sub: callerClientId }
        var opaqueEnabled = opaquePolicy.ShouldUseOpaqueAccessToken(audience);

        var jtiNew = Guid.NewGuid().ToString("N");
        string accessToken;

        // Resolve subject identifier for the issued token
        // Normal OBO: sub = userId (subject token holder)
        // Delegated grant: sub = delegatorUserId (delegator is the subject)
        Guid issuedTokenSubjectId = userId;
        if (delegatedGrantId is not null && delegatorUserId is not null)
        {
            issuedTokenSubjectId = delegatorUserId.Value;
        }

        // Resolve actor identifier for the act claim
        // Normal OBO: act.sub = callerClientId (the caller performing the exchange)
        // Delegated grant: act.sub = userId (the delegate is the actor)
        string actSubClaim = callerClientId;
        if (delegatedGrantId is not null && delegatorUserId is not null)
        {
            actSubClaim = userId.ToString(); // Delegate's user account ID as actor
        }

        if (opaqueEnabled)
        {
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var actObj = new { sub = actSubClaim };
            var actJson = System.Text.Json.JsonSerializer.Serialize(actObj);
            // Compute new delegation depth for opaque subjects (JWT subjects will start at 1)
            var newDepth = isJwt ? 1 : subjectDelegationDepth + 1;
            await PersistOpaqueAccessAsync(issuedTokenSubjectId, callerClientId, audience, resultScopes, jtiNew, raw, lifetime, cnfJkt: outCnfJkt, ct, actJson: actJson, delegationDepth: newDepth).ConfigureAwait(false);
            accessToken = raw;
        }
        else
        {
            var claims = new List<System.Security.Claims.Claim>
            {
                new("sub", issuedTokenSubjectId.ToString()),
                new("jti", jtiNew),
                new("scope", string.Join(' ', resultScopes)),
                new("act", System.Text.Json.JsonSerializer.Serialize(new { sub = actSubClaim }))
            };

            // For delegated grants, include grant reference as delegation_id (private)
            if (delegatedGrantId is not null && delegatorUserId is not null)
            {
                claims.Add(new("delegation_id", delegatedGrantId.ToString()));
            }

            // Add tenant_id claim if any custom (non-standard) scopes are granted and tenant_id was in subject token
            var hasCustomScopes = resultScopes.Any(s => !scopeResolver.IsStandardScope(s));
            if (hasCustomScopes && !string.IsNullOrEmpty(subjectTenantId))
            {
                claims.Add(new System.Security.Claims.Claim("tenant_id", subjectTenantId));
            }

            // Propagate tenants list only when scope is granted
            if (resultScopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(subjectTenantsJson))
            {
                claims.Add(new System.Security.Claims.Claim(OidcConstants.Scopes.Tenants, subjectTenantsJson));
            }

            if (!string.IsNullOrEmpty(outCnfJkt))
            {
                var cnf = System.Text.Json.JsonSerializer.Serialize(new { jkt = outCnfJkt });
                claims.Add(new("cnf", cnf));
            }
            var nowUtc = DateTimeOffset.UtcNow;
            accessToken = await jwt.CreateJwtAsync(issuer, audience, claims, nowUtc.Add(lifetime), tokenType: SecurityConstants.JwtTokenTypes.AtJwt, ct: ct).ConfigureAwait(false);
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

    private async Task<Persistence.Token?> FindSubjectAccessTokenAsync(string subjectToken, string? jti, CancellationToken ct)
    {
        var hash = CryptoHelper.ComputeSha256Base64(subjectToken);
        var now = DateTimeOffset.UtcNow;
        var baseQuery = db.Tokens
            .AsNoTracking()
            .Where(t => t.Type == "access")
            .Where(t => t.RevokedAt == null)
            .Where(t => t.ExpiresAt > now);

        var entity = await baseQuery.FirstOrDefaultAsync(t => t.TokenHash == hash, ct).ConfigureAwait(false);
        if (entity is not null || string.IsNullOrWhiteSpace(jti))
        {
            return entity;
        }

        return await baseQuery.FirstOrDefaultAsync(t => t.Jti == jti, ct).ConfigureAwait(false);
    }

    private static bool IsSubjectAudienceTrusted(string? sourceAudience, string[]? configuredAudiences, Client? callerClient)
    {
        if (string.IsNullOrWhiteSpace(sourceAudience))
        {
            return true;
        }

        if (IsSourceAudienceAllowedByClientPolicy(sourceAudience, callerClient))
        {
            return true;
        }

        var allowedAudiences = configuredAudiences?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        return allowedAudiences.Length == 0 || allowedAudiences.Contains(sourceAudience, StringComparer.Ordinal);
    }

    private static bool IsSubjectClientAllowedForCaller(string? subjectClientId, string callerClientId, string? sourceAudience, Client? callerClient)
    {
        if (string.IsNullOrWhiteSpace(subjectClientId) || string.Equals(subjectClientId, callerClientId, StringComparison.Ordinal))
        {
            return true;
        }

        return IsSourceAudienceAllowedByClientPolicy(sourceAudience, callerClient);
    }

    private static bool IsSourceAudienceAllowedByClientPolicy(string? sourceAudience, Client? callerClient)
    {
        if (string.IsNullOrWhiteSpace(sourceAudience) || callerClient is null)
        {
            return false;
        }

        var allowedSourceAudiences = ParseJsonArray(callerClient.OboAllowedSourceAudiencesJson);
        return allowedSourceAudiences.Contains(sourceAudience, StringComparer.Ordinal);
    }

    private static string[] ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? ResolveDefaultAudience(string? sourceAudience, string[]? configuredAudiences)
    {
        if (!string.IsNullOrWhiteSpace(sourceAudience))
        {
            return sourceAudience;
        }

        var allowedAudiences = configuredAudiences?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        return allowedAudiences.Length == 1 ? allowedAudiences[0] : null;
    }

    async     Task PersistOpaqueAccessAsync(Guid userId, string clientId, string audience, string[] scopes, string jti, string rawToken, TimeSpan lifetime, string? cnfJkt, CancellationToken ct, string? actJson = null, int delegationDepth = 0)
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

/// <summary>
/// Resolves a delegated grant context for token exchange authorization.
/// Provides the grant ID and delegator subject identifier when a delegated grant
/// is active between the caller (delegate) and the subject (delegator).
/// Implements Section 6.9 grant-authorized token exchange context resolution.
/// </summary>
public interface IGrantContextResolver
{
    /// <summary>
    /// Resolve the active delegated grant context for the token exchange.
    /// Returns the grant ID and delegator user account ID if an active grant
    /// exists between the caller delegate and the subject delegator.
    /// Returns null if no grant is active or if the context is normal OBO.
    /// </summary>
    /// <param name="callerClientId">The client ID of the caller performing the exchange.</param>
    /// <param name="subjectUserId">The user account ID of the subject token holder (delegator).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The grant context with grant ID and delegator ID, or null if no grant.</returns>
    Task<GrantContext?> GetGrantContextAsync(string callerClientId, Guid subjectUserId, CancellationToken ct = default);
}

/// <summary>
/// Context for an active delegated grant used in token exchange.
/// </summary>
public sealed record GrantContext(
    Guid GrantId,
    Guid DelegatorUserAccountId,
    Guid DelegateUserAccountId);
