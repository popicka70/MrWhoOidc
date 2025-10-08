using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

public interface ITokenService
{
    Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, CancellationToken ct = default);
    Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, CancellationToken ct = default);
    // New: client credentials (M2M)
    Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default);
    // New: Token Exchange (RFC 8693) MVP
    Task<(bool ok, object? payload, string? error, int status)> ExchangeTokenAsync(
        string subjectToken,
        string? subjectTokenType,
        string? requestedTokenType,
        string? requestedAudience,
        string[] requestedScopes,
        string callerClientId,
        string issuer,
        string? dpopJkt = null,
        CancellationToken ct = default);
}

internal sealed class TokenService(AuthDbContext db, IJwtService jwt, IRefreshTokenService refreshTokens, IOptions<AuthOptions> authOptions, IAuthorizationCodeMetadataStore meta, ITokenValidator validator, ITenantSettingsService settingsService, IOboPolicyService? oboPolicy = null) : ITokenService
{
    private readonly ITenantSettingsService _settingsService = settingsService;
    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, CancellationToken ct = default)
    {
        var entity = await db.AuthorizationCodes.FirstOrDefaultAsync(c => c.Code == code, ct).ConfigureAwait(false);
        if (entity is null || entity.Consumed || entity.ExpiresAt < DateTimeOffset.UtcNow)
            return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);

        if (!string.Equals(entity.RedirectUri, redirectUri, StringComparison.Ordinal))
            return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);

        if (!string.Equals(entity.ClientId, clientId, StringComparison.Ordinal))
            return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);

        // Validate PKCE S256
        if (!string.IsNullOrEmpty(entity.CodeChallenge))
        {
            var s256 = CryptoHelper.ComputePkceS256(codeVerifier);
            if (!string.Equals(s256, entity.CodeChallenge, StringComparison.Ordinal))
                return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant }, OAuthConstants.ErrorCodes.InvalidGrant, 400);
        }

        var scopes = JsonSerializer.Deserialize<string[]>(entity.ScopesJson) ?? Array.Empty<string>();

        // RFC 8707: prefer resource indicator as access token audience when present
        string audience;
        if (meta.TryGetResource(code, out var resource) && !string.IsNullOrWhiteSpace(resource))
        {
            audience = resource;
        }
        else
        {
            audience = (authOptions.Value.ApiAudiences?.FirstOrDefault()) ?? "api";
        }

        // Determine opaque access token issuance
        var opaqueEnabled = authOptions.Value.OpaqueAccessTokens?.Enabled == true &&
            (authOptions.Value.OpaqueAccessTokens.Audiences is null || authOptions.Value.OpaqueAccessTokens.Audiences.Length == 0 ||
             authOptions.Value.OpaqueAccessTokens.Audiences.Contains(audience, StringComparer.Ordinal));

        // Lookup user and client to compute role claims and realm
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == entity.UserId, ct).ConfigureAwait(false);
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        string? realmName = null;
        string[] roleNames = Array.Empty<string>();
        if (client is not null)
        {
            realmName = await db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (scopes.Contains("roles"))
            {
                // Roles limited to this client and its realm
                var realmRoleNamesQuery = db.UserRealmRoleAssignments.AsNoTracking()
                    .Where(a => a.UserId == entity.UserId && a.RealmId == client.RealmId && a.IsActive)
                    .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                    .Where(x => x.r.IsActive)
                    .Select(x => x.r.Name);

                var clientRoleNamesQuery = db.UserClientRoleAssignments.AsNoTracking()
                    .Where(a => a.UserId == entity.UserId && a.ClientId == client.Id && a.IsActive)
                    .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                    .Where(x => x.r.IsActive)
                    .Select(x => x.r.Name);

                roleNames = await realmRoleNamesQuery.Union(clientRoleNamesQuery).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
            }
        }

        // Upstream context captured during /authorize
        meta.TryGetUpstream(code, out var upstreamIdp, out var upstreamAcr, out var upstreamAmrStr);
        var upstreamAmrs = string.IsNullOrWhiteSpace(upstreamAmrStr)
            ? Array.Empty<string>()
            : upstreamAmrStr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Mapped claims captured during /authorize (from external mapping)
        meta.TryGetMappedClaims(code, out var mappedClaimsReadOnly);
        var mappedClaims = mappedClaimsReadOnly is null ? new Dictionary<string, string>() : new Dictionary<string, string>(mappedClaimsReadOnly);

        // Precompute combined AMR set from upstream and mapped (if allow-listed)
        var combinedAmr = new HashSet<string>(StringComparer.Ordinal);
        if (authOptions.Value.EmitAmrInAccessToken || authOptions.Value.EmitAmrInIdToken)
        {
            foreach (var a in upstreamAmrs) combinedAmr.Add(a);
            // If mapping contains an 'amr' claim and it's allow-listed for either token, merge values
            if (mappedClaims.TryGetValue("amr", out var mappedAmr) && !string.IsNullOrWhiteSpace(mappedAmr))
            {
                foreach (var a in mappedAmr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    combinedAmr.Add(a);
            }
        }

        // Load tenant settings once for all token generation
        var settings = await _settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
        var accessTokenLifetime = TimeSpan.FromSeconds(settings.Tokens?.AccessTokenLifetimeSeconds ?? 3600); // Default: 1 hour
        var idTokenLifetime = TimeSpan.FromSeconds(settings.Tokens?.IdTokenLifetimeSeconds ?? 3600); // Default: 1 hour

        string accessToken;
        if (opaqueEnabled)
        {
            // Create opaque token (random 256-bit), persist with hash
            var jti = Guid.NewGuid().ToString("N");
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await PersistOpaqueAccessAsync(entity.UserId, clientId, audience, scopes, jti, raw, accessTokenLifetime, dpopJkt, ct).ConfigureAwait(false);
            accessToken = raw;
        }
        else
        {
            // Build JWT access token first (include scopes claim) and include jti
            var jti = Guid.NewGuid().ToString("N");
            var accessClaims = new List<System.Security.Claims.Claim>
            {
                new("sub", entity.UserId.ToString()),
                new("scope", string.Join(' ', scopes)),
                new("jti", jti)
            };
            if (!string.IsNullOrEmpty(dpopJkt))
            {
                var cnf = JsonSerializer.Serialize(new { jkt = dpopJkt });
                accessClaims.Add(new("cnf", cnf));
            }
            if (scopes.Contains("roles") && roleNames.Length > 0)
            {
                foreach (var r in roleNames) accessClaims.Add(new("roles", r));
            }
            if (!string.IsNullOrEmpty(realmName))
            {
                accessClaims.Add(new("realm", realmName));
            }
            // Propagate upstream context into access token when available
            if (!string.IsNullOrWhiteSpace(upstreamIdp)) accessClaims.Add(new("idp", upstreamIdp!));
            if (!string.IsNullOrWhiteSpace(upstreamAcr)) accessClaims.Add(new("acr", upstreamAcr!));
            if (authOptions.Value.EmitAmrInAccessToken)
            {
                foreach (var amr in combinedAmr) accessClaims.Add(new("amr", amr));
            }

            // Propagate mapped claims into access token when allow-listed (skip amr to avoid conflicts)
            var allowAccess = authOptions.Value.PropagateMappedClaimsToAccessToken ?? Array.Empty<string>();
            if (allowAccess.Length > 0 && mappedClaims.Count > 0)
            {
                foreach (var name in allowAccess)
                {
                    if (string.Equals(name, "amr", StringComparison.Ordinal)) continue; // handled above
                    if (mappedClaims.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
                    {
                        accessClaims.Add(new(name, val));
                    }
                }
            }

            accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.Add(accessTokenLifetime));
        }

        // Compute at_hash per OIDC (left-most half of SHA-256 of access token)
        var atHash = ComputeAtHash(accessToken);

        // Prepare ID token claims, include profile/email if requested
        var idClaims = new List<System.Security.Claims.Claim>
        {
            new("sub", entity.UserId.ToString())
        };

        if (user is not null)
        {
            if (scopes.Contains("profile") && !string.IsNullOrEmpty(user.Name))
                idClaims.Add(new("name", user.Name));
            if (scopes.Contains("email") && !string.IsNullOrEmpty(user.Email))
            {
                idClaims.Add(new("email", user.Email));
                idClaims.Add(new("email_verified", user.EmailVerified ? "true" : "false"));
            }
            if (scopes.Contains("roles") && roleNames.Length > 0)
            {
                foreach (var r in roleNames) idClaims.Add(new("roles", r));
            }
            if (!string.IsNullOrEmpty(realmName))
            {
                idClaims.Add(new("realm", realmName));
            }
        }

        // Pull auth_time from metadata store if available
        DateTimeOffset? authTime = null;
        if (meta.TryGetAuthTime(code, out var at)) authTime = at;

        // Propagate upstream context into ID token as well
        if (!string.IsNullOrWhiteSpace(upstreamIdp)) idClaims.Add(new("idp", upstreamIdp!));
        if (!string.IsNullOrWhiteSpace(upstreamAcr)) idClaims.Add(new("acr", upstreamAcr!));
        if (authOptions.Value.EmitAmrInIdToken)
        {
            foreach (var amr in combinedAmr) idClaims.Add(new("amr", amr));
        }

        // Propagate mapped claims into ID token when allow-listed (skip amr to avoid conflicts)
        var allowId = authOptions.Value.PropagateMappedClaimsToIdToken ?? Array.Empty<string>();
        if (allowId.Length > 0 && mappedClaims.Count > 0)
        {
            foreach (var name in allowId)
            {
                if (string.Equals(name, "amr", StringComparison.Ordinal)) continue; // handled above
                if (mappedClaims.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
                {
                    idClaims.Add(new(name, val));
                }
            }
        }

        // Include sid for front-channel logout if available
        if (meta.TryGetSid(code, out var sid) && !string.IsNullOrWhiteSpace(sid))
        {
            idClaims.Add(new("sid", sid!));
        }

        var idToken = jwt.CreateJwt(
            issuer,
            clientId,
            idClaims,
            DateTimeOffset.UtcNow.Add(idTokenLifetime),
            nonce: entity.Nonce,
            accessTokenHash: atHash,
            authTime: authTime
        );

        var (refreshToken, _) = await refreshTokens.CreateRefreshTokenAsync(entity.UserId, clientId, scopes, ipAddress, userAgent, ct).ConfigureAwait(false);

        entity.Consumed = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Cleanup
        meta.Remove(code);

        var payload = new
        {
            access_token = accessToken,
            id_token = idToken,
            refresh_token = refreshToken,
            token_type = "Bearer",
            expires_in = 900,
            scope = string.Join(' ', scopes)
        };
        return (true, payload, null, 200);
    }

    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, CancellationToken ct = default)
    {
        // Load tenant settings
        var settings = await _settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
        var accessTokenLifetime = TimeSpan.FromSeconds(settings.Tokens?.AccessTokenLifetimeSeconds ?? 3600);

        var hash = Hash(refreshToken);
        var tokenEntity = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == "refresh" && t.RevokedAt == null, ct).ConfigureAwait(false);
        if (tokenEntity is null || tokenEntity.ExpiresAt < DateTimeOffset.UtcNow || !string.Equals(tokenEntity.ClientId, clientId, StringComparison.Ordinal))
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);

        var scopes = JsonSerializer.Deserialize<string[]>(tokenEntity.ScopesJson) ?? Array.Empty<string>();
        var audience = (authOptions.Value.ApiAudiences?.FirstOrDefault()) ?? "api";

        // Opaque issuance check matches authorization_code path
        var opaqueEnabled = authOptions.Value.OpaqueAccessTokens?.Enabled == true &&
            (authOptions.Value.OpaqueAccessTokens.Audiences is null || authOptions.Value.OpaqueAccessTokens.Audiences.Length == 0 ||
             authOptions.Value.OpaqueAccessTokens.Audiences.Contains(audience, StringComparer.Ordinal));

        // Client and realm for roles
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        string? realmName = null;
        string[] roleNames = Array.Empty<string>();
        if (client is not null)
        {
            realmName = await db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (scopes.Contains("roles"))
            {
                var realmRoleNamesQuery = db.UserRealmRoleAssignments.AsNoTracking()
                    .Where(a => a.UserId == tokenEntity.UserId && a.RealmId == client.RealmId && a.IsActive)
                    .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                    .Where(x => x.r.IsActive)
                    .Select(x => x.r.Name);

                var clientRoleNamesQuery = db.UserClientRoleAssignments.AsNoTracking()
                    .Where(a => a.UserId == tokenEntity.UserId && a.ClientId == client.Id && a.IsActive)
                    .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
                    .Where(x => x.r.IsActive)
                    .Select(x => x.r.Name);

                roleNames = await realmRoleNamesQuery.Union(clientRoleNamesQuery).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
            }
        }

        string accessToken;
        if (opaqueEnabled)
        {
            var jti = Guid.NewGuid().ToString("N");
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await PersistOpaqueAccessAsync(tokenEntity.UserId, clientId, audience, scopes, jti, raw, TimeSpan.FromMinutes(15), dpopJkt, ct).ConfigureAwait(false);
            accessToken = raw;
        }
        else
        {
            var jti = Guid.NewGuid().ToString("N");
            var accessClaims = new List<System.Security.Claims.Claim>
            {
                new("sub", tokenEntity.UserId.ToString()),
                new("scope", string.Join(' ', scopes)),
                new("jti", jti)
            };
            if (!string.IsNullOrEmpty(dpopJkt))
            {
                var cnf = JsonSerializer.Serialize(new { jkt = dpopJkt });
                accessClaims.Add(new("cnf", cnf));
            }
            if (scopes.Contains("roles") && roleNames.Length > 0)
            {
                foreach (var r in roleNames) accessClaims.Add(new("roles", r));
            }
            if (!string.IsNullOrEmpty(realmName))
            {
                accessClaims.Add(new("realm", realmName));
            }
            accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.Add(accessTokenLifetime));
        }

        // Rotation: create new refresh token and revoke the old one
        var (newRefresh, _) = await refreshTokens.CreateRefreshTokenAsync(tokenEntity.UserId, clientId, scopes, ipAddress, userAgent, ct).ConfigureAwait(false);
        tokenEntity.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var payload = new
        {
            access_token = accessToken,
            refresh_token = newRefresh,
            token_type = "Bearer",
            expires_in = 900,
            scope = string.Join(' ', scopes)
        };
        return (true, payload, null, 200);
    }

    public async Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default)
    {
        // Load tenant settings for default token lifetime
        var settings = await _settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
        var defaultAccessTokenLifetime = TimeSpan.FromSeconds(settings.Tokens?.AccessTokenLifetimeSeconds ?? 3600);

        // Resolve client and its policy/scopes
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct).ConfigureAwait(false);
        if (client is null)
        {
            return (false, new { error = "unauthorized_client" }, "unauthorized_client", 400);
        }

        // Determine allowed audiences: per-client override, else global server setting
        string[] perClientAudiences = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(client.M2MAllowedAudiencesJson))
        {
            try { perClientAudiences = JsonSerializer.Deserialize<string[]>(client.M2MAllowedAudiencesJson) ?? Array.Empty<string>(); }
            catch { perClientAudiences = Array.Empty<string>(); }
        }
        var globalAudiences = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
        var allowedAudiences = perClientAudiences.Length > 0 ? perClientAudiences : globalAudiences;
        if (allowedAudiences.Length > 0 && !allowedAudiences.Contains(audience, StringComparer.Ordinal))
        {
            return (false, new { error = "invalid_target", error_description = "audience not allowed" }, "invalid_target", 400);
        }

        // Resolve allowed scopes from mapping table
        var allowedScopeNames = await db.ClientScopes.AsNoTracking()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        // Default: if no mapping configured, allow none (empty)
        var granted = new List<string>();
        if (requestedScopes is { Length: > 0 })
        {
            foreach (var s in requestedScopes)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                // Exclude OIDC user scopes from M2M
                if (string.Equals(s, "openid", StringComparison.Ordinal) || string.Equals(s, "offline_access", StringComparison.Ordinal))
                    continue;
                if (allowedScopeNames.Contains(s, StringComparer.Ordinal))
                    granted.Add(s);
            }
        }
        else
        {
            // If no scopes requested, grant nothing by default
        }

        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", clientId),
            new("client_id", clientId),
            new("jti", jti)
        };
        if (granted.Count > 0)
        {
            claims.Add(new("scope", string.Join(' ', granted)));
        }
        if (!string.IsNullOrEmpty(dpopJkt))
        {
            var cnf = JsonSerializer.Serialize(new { jkt = dpopJkt });
            claims.Add(new("cnf", cnf));
        }

        // Optional realm claim
        var realmName = await db.Realms.AsNoTracking().Where(r => r.Id == client.RealmId).Select(r => r.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(realmName))
        {
            claims.Add(new("realm", realmName));
        }

        // Lifetime override per client for M2M
        var lifetime = (client.M2MAccessTokenLifetimeSeconds.HasValue && client.M2MAccessTokenLifetimeSeconds.Value > 0)
            ? TimeSpan.FromSeconds(client.M2MAccessTokenLifetimeSeconds.Value)
            : defaultAccessTokenLifetime;

        // Issue JWT access token (opaque not supported for M2M yet)
        var expiry = DateTimeOffset.UtcNow.Add(lifetime);
        var accessToken = jwt.CreateJwt(issuer, audience, claims, expiry);

        var payload = new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = (int)lifetime.TotalSeconds,
            scope = granted.Count > 0 ? string.Join(' ', granted) : null
        };
        return (true, payload, null, 200);
    }

    async Task PersistOpaqueAccessAsync(Guid userId, string clientId, string audience, string[] scopes, string jti, string rawToken, TimeSpan lifetime, string? cnfJkt, CancellationToken ct, string? actJson = null, int delegationDepth = 0)
    {
        var hash = Hash(rawToken);
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

    // Legacy helper methods kept for compatibility - delegate to CryptoHelper
    static string ComputeS256(string verifier) => CryptoHelper.ComputePkceS256(verifier);
    static string ComputeAtHash(string accessToken) => CryptoHelper.ComputeLeftHalfSha256Base64Url(accessToken);
    static string Hash(string value) => CryptoHelper.ComputeSha256Base64(value);

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
        var settings = await _settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
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
            var hash = Hash(subjectToken);
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
}
