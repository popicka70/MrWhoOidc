using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Entitlements;
using MrWhoOidc.Auth.Entitlements.Contracts;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

public interface ITokenService
{
    Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default);
    Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default);
    // New: client credentials (M2M)
    Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default);
}

internal sealed class TokenService(AuthDbContext db, IJwtService jwt, IRefreshTokenService refreshTokens, IOptions<AuthOptions> authOptions, IAuthorizationCodeMetadataStore meta, ITenantSettingsService settingsService, IScopeResolver scopeResolver, IEntitlementsProvider entitlementsProvider) : ITokenService
{
    private readonly ITenantSettingsService _settingsService = settingsService;
    private readonly IEntitlementsProvider _entitlementsProvider = entitlementsProvider;

    private static readonly JsonSerializerOptions EntitlementsJsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<(bool ok, object? payload, string? error, int status)>(async () =>
        {
            using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
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

        // Fail-closed product-scope granting and claim injection (licensed products)
        Guid? tenantIdForEntitlements = null;
        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            tenantIdForEntitlements = tenantId;
        }
        else if (user is not null && user.TenantId != Guid.Empty)
        {
            tenantIdForEntitlements = user.TenantId;
        }

        var (scopesFiltered, entitlementsClaimJson) = await ApplyProductEntitlementsAsync(
            subjectId: entity.UserId.ToString(),
            tenantId: tenantIdForEntitlements,
            requestedScopes: scopes,
            issuer: issuer,
            ct: ct).ConfigureAwait(false);
        scopes = scopesFiltered;

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
                new(OidcConstants.Claims.Subject, entity.UserId.ToString()),
                new(OAuthConstants.Parameters.Scope, string.Join(' ', scopes)),
                new("jti", jti)
            };

            if (!string.IsNullOrWhiteSpace(entitlementsClaimJson))
            {
                accessClaims.Add(new("entitlements", entitlementsClaimJson));
            }
            
            // Add tenant_id claim if any custom (non-standard) scopes are granted
            var hasCustomScopes = scopes.Any(s => !scopeResolver.IsStandardScope(s));
            if (hasCustomScopes && user?.TenantId != Guid.Empty)
            {
                accessClaims.Add(new(OidcConstants.Claims.TenantId, user!.TenantId.ToString()));
            }
            
            if (!string.IsNullOrEmpty(dpopJkt))
            {
                var cnf = JsonSerializer.Serialize(new { jkt = dpopJkt });
                accessClaims.Add(new(OidcConstants.Claims.Cnf, cnf));
            }
            if (scopes.Contains(OidcConstants.Scopes.Roles) && roleNames.Length > 0)
            {
                foreach (var r in roleNames) accessClaims.Add(new(OidcConstants.Claims.Roles, r));
            }
            if (!string.IsNullOrEmpty(realmName))
            {
                accessClaims.Add(new(OidcConstants.Claims.Realm, realmName));
            }
            // Propagate upstream context into access token when available
            if (!string.IsNullOrWhiteSpace(upstreamIdp)) accessClaims.Add(new(OidcConstants.Claims.Idp, upstreamIdp!));
            if (!string.IsNullOrWhiteSpace(upstreamAcr)) accessClaims.Add(new(OidcConstants.Claims.Acr, upstreamAcr!));
            if (authOptions.Value.EmitAmrInAccessToken)
            {
                foreach (var amr in combinedAmr) accessClaims.Add(new(OidcConstants.Claims.Amr, amr));
            }

            // Propagate mapped claims into access token when allow-listed (skip amr to avoid conflicts)
            var allowAccess = authOptions.Value.PropagateMappedClaimsToAccessToken ?? Array.Empty<string>();
            if (allowAccess.Length > 0 && mappedClaims.Count > 0)
            {
                foreach (var name in allowAccess)
                {
                    if (string.Equals(name, OidcConstants.Claims.Amr, StringComparison.Ordinal)) continue; // handled above
                    if (mappedClaims.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
                    {
                        accessClaims.Add(new(name, val));
                    }
                }
            }

            accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.Add(accessTokenLifetime), tokenType: SecurityConstants.JwtTokenTypes.AtJwt);
        }

        // Compute at_hash per OIDC (left-most half of SHA-256 of access token)
        var atHash = ComputeAtHash(accessToken);

        // Prepare ID token claims, include profile/email if requested
        var idClaims = new List<System.Security.Claims.Claim>
        {
            new(OidcConstants.Claims.Subject, entity.UserId.ToString())
        };

        if (user is not null)
        {
            if (scopes.Contains(OidcConstants.Scopes.Profile) && !string.IsNullOrEmpty(user.Name))
                idClaims.Add(new(OidcConstants.Claims.Name, user.Name));
            if (scopes.Contains(OidcConstants.Scopes.Email) && !string.IsNullOrEmpty(user.Email))
            {
                idClaims.Add(new(OidcConstants.Claims.Email, user.Email));
                idClaims.Add(new(OidcConstants.Claims.EmailVerified, user.EmailVerified ? "true" : "false"));
            }
            if (scopes.Contains(OidcConstants.Scopes.Roles) && roleNames.Length > 0)
            {
                foreach (var r in roleNames) idClaims.Add(new(OidcConstants.Claims.Roles, r));
            }
            if (!string.IsNullOrEmpty(realmName))
            {
                idClaims.Add(new(OidcConstants.Claims.Realm, realmName));
            }
        }

        // Pull auth_time from metadata store if available
        DateTimeOffset? authTime = null;
        if (meta.TryGetAuthTime(code, out var at)) authTime = at;

        // Propagate upstream context into ID token as well
        if (!string.IsNullOrWhiteSpace(upstreamIdp)) idClaims.Add(new(OidcConstants.Claims.Idp, upstreamIdp!));
        if (!string.IsNullOrWhiteSpace(upstreamAcr)) idClaims.Add(new(OidcConstants.Claims.Acr, upstreamAcr!));
        if (authOptions.Value.EmitAmrInIdToken)
        {
            foreach (var amr in combinedAmr) idClaims.Add(new(OidcConstants.Claims.Amr, amr));
        }

        // Propagate mapped claims into ID token when allow-listed (skip amr to avoid conflicts)
        var allowId = authOptions.Value.PropagateMappedClaimsToIdToken ?? Array.Empty<string>();
        if (allowId.Length > 0 && mappedClaims.Count > 0)
        {
            foreach (var name in allowId)
            {
                if (string.Equals(name, OidcConstants.Claims.Amr, StringComparison.Ordinal)) continue; // handled above
                if (mappedClaims.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val))
                {
                    idClaims.Add(new(name, val));
                }
            }
        }

        // Include sid for front-channel logout if available
        if (meta.TryGetSid(code, out var sid) && !string.IsNullOrWhiteSpace(sid))
        {
            idClaims.Add(new(OidcConstants.Claims.Sid, sid!));
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
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        // Cleanup
        meta.Remove(code);

        var payload = new
        {
            access_token = accessToken,
            id_token = idToken,
            refresh_token = refreshToken,
            token_type = OAuthConstants.TokenTypes.Bearer,
            expires_in = (int)accessTokenLifetime.TotalSeconds,
            scope = string.Join(' ', scopes)
        };
        return (true, payload, null, 200);
            }
            catch
            {
                throw;
            }
        });
    }

    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, string? dpopJkt = null, string? ipAddress = null, string? userAgent = null, Guid? tenantId = null, CancellationToken ct = default)
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

        // Fail-closed product-scope granting and claim injection (licensed products)
        var userForTenant = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == tokenEntity.UserId, ct).ConfigureAwait(false);

        Guid? tenantIdForEntitlements = null;
        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            tenantIdForEntitlements = tenantId;
        }
        else if (userForTenant is not null && userForTenant.TenantId != Guid.Empty)
        {
            tenantIdForEntitlements = userForTenant.TenantId;
        }

        var (scopesFiltered, entitlementsClaimJson) = await ApplyProductEntitlementsAsync(
            subjectId: tokenEntity.UserId.ToString(),
            tenantId: tenantIdForEntitlements,
            requestedScopes: scopes,
            issuer: issuer,
            ct: ct).ConfigureAwait(false);
        scopes = scopesFiltered;

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
            await PersistOpaqueAccessAsync(tokenEntity.UserId, clientId, audience, scopes, jti, raw, accessTokenLifetime, dpopJkt, ct).ConfigureAwait(false);
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

            if (!string.IsNullOrWhiteSpace(entitlementsClaimJson))
            {
                accessClaims.Add(new("entitlements", entitlementsClaimJson));
            }
            
            // Add tenant_id claim if any custom (non-standard) scopes are granted
            var hasCustomScopes = scopes.Any(s => !scopeResolver.IsStandardScope(s));
            if (hasCustomScopes)
            {
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == tokenEntity.UserId, ct).ConfigureAwait(false);
                if (user?.TenantId != Guid.Empty)
                {
                    accessClaims.Add(new("tenant_id", user!.TenantId.ToString()));
                }
            }
            
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
            accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.Add(accessTokenLifetime), tokenType: SecurityConstants.JwtTokenTypes.AtJwt);
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
            expires_in = (int)accessTokenLifetime.TotalSeconds,
            scope = string.Join(' ', scopes)
        };
        return (true, payload, null, 200);
    }

    public async Task<(bool ok, object? payload, string? error, int status)> CreateClientCredentialsTokenAsync(
        string clientId, string audience, string[] requestedScopes, string issuer, string? dpopJkt = null, CancellationToken ct = default)
    {
        // Fail-closed for product scopes on client_credentials.
        if (requestedScopes.Any(ProductScopeClassifier.IsProductScope))
        {
            return (false, new { error = OAuthConstants.ErrorCodes.InvalidScope, error_description = "product scopes are not supported for client_credentials" }, OAuthConstants.ErrorCodes.InvalidScope, 400);
        }

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
            
            // Add tenant_id claim if any custom (non-standard) scopes are granted
            var hasCustomScopes = granted.Any(s => !scopeResolver.IsStandardScope(s));
            if (hasCustomScopes && client.TenantId != Guid.Empty)
            {
                claims.Add(new("tenant_id", client.TenantId.ToString()));
            }
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
        var accessToken = jwt.CreateJwt(issuer, audience, claims, expiry, tokenType: SecurityConstants.JwtTokenTypes.AtJwt);

        var payload = new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = (int)lifetime.TotalSeconds,
            scope = granted.Count > 0 ? string.Join(' ', granted) : null
        };
        return (true, payload, null, 200);
    }

    private async Task<(string[] scopes, string? entitlementsClaimJson)> ApplyProductEntitlementsAsync(
        string subjectId,
        Guid? tenantId,
        string[] requestedScopes,
        string issuer,
        CancellationToken ct)
    {
        var productScopes = requestedScopes
            .Where(ProductScopeClassifier.IsProductScope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (productScopes.Length == 0)
        {
            return (requestedScopes, null);
        }

        string? tenantIdStr = tenantId.HasValue && tenantId.Value != Guid.Empty ? tenantId.Value.ToString() : null;

        IReadOnlyDictionary<string, Entitlement> entitlements;
        try
        {
            entitlements = await _entitlementsProvider.GetEffectiveEntitlementsAsync(subjectId, tenantIdStr, productScopes, issuer, ct).ConfigureAwait(false);
        }
        catch
        {
            entitlements = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase);
        }

        var grantedProducts = productScopes.Where(p => entitlements.ContainsKey(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredScopes = requestedScopes
            .Where(s => !ProductScopeClassifier.IsProductScope(s) || grantedProducts.Contains(s))
            .ToArray();

        if (grantedProducts.Count == 0)
        {
            return (filteredScopes, null);
        }

        var claimObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in grantedProducts)
        {
            var e = entitlements[p];
            claimObj[p] = new { tier = e.Tier, source = e.Source, licenseId = e.LicenseId, status = e.Status };
        }

        var json = JsonSerializer.Serialize(claimObj, EntitlementsJsonOptions);
        return (filteredScopes, json);
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
}
