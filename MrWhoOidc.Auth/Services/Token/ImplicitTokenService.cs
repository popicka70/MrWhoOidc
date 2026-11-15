using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.Auth.Services.KeyManagement;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.Auth.Utils;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Request context for implicit-flow (OIDC Core §3.1 / hybrid-less id_token, id_token token) token issuance.
/// The authorization endpoint supplies everything the browser session knows; this service resolves the user,
/// builds the ID token (and access token for id_token token) and persists the access token row.
/// </summary>
public sealed record ImplicitTokenRequest(
    Persistence.Client Client,
    Guid UserId,
    AuthorizeValidationResult Validated,
    string Issuer,
    Guid TenantId,
    DateTimeOffset? AuthTime = null,
    string? SessionId = null,
    string? UpstreamIdp = null,
    string? UpstreamAcr = null,
    string[]? UpstreamAmr = null,
    string? IpAddress = null,
    string? UserAgent = null
);

public sealed record ImplicitTokenResult(
    bool Ok,
    string? Error = null,
    string? ErrorDescription = null,
    string? IdToken = null,
    string? AccessToken = null,
    int? ExpiresInSeconds = null
);

public interface IImplicitTokenService
{
    /// <summary>
    /// Issues the implicit-flow response tokens. The caller owns the transaction; the access token row
    /// is persisted via the tracked <c>AuthDbContext</c> so it commits/rolls back with the caller's transaction.
    /// </summary>
    Task<ImplicitTokenResult> IssueAsync(ImplicitTokenRequest request, CancellationToken ct = default);
}

/// <summary>
/// OIDC implicit-flow token issuer (response_type id_token, id_token token). Mirrors the ID token construction
/// of the authorization-code exchange (scope-gated claims, claims-parameter selection, pairwise subjects,
/// at_hash, nonce, auth_time) but issues tokens in the front channel without an authorization code.
/// </summary>
public sealed class ImplicitTokenService(
    AuthDbContext db,
    IJwtService jwt,
    ICachedKeyProvider keyProvider,
    IPairwiseSubjectService pairwiseSubjectService,
    IAccessTokenClaimBuilder claimBuilder,
    ITokenLifetimeResolver lifetimeResolver,
    IOpaqueTokenPolicy opaquePolicy,
    ITenantSettingsService settingsService,
    IOptions<AuthOptions> authOptions,
    ILogger<ImplicitTokenService> logger,
    IClientJwksProvider? clientJwksProvider = null,
    IHttpClientFactory? httpClientFactory = null,
    IJwksCache? jwksCache = null,
    ISecretProtector? secretProtector = null) : IImplicitTokenService
{
    private readonly IClientJwksProvider _clientJwksProvider = clientJwksProvider ?? new ClientJwksResolver();

    public async Task<ImplicitTokenResult> IssueAsync(ImplicitTokenRequest request, CancellationToken ct = default)
    {
        var client = request.Client;
        var valid = request.Validated;
        var scopes = valid.Scopes ?? Array.Empty<string>();
        var includeAccessToken = (valid.ResponseType ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(OAuthConstants.ResponseTypes.Token, StringComparer.Ordinal);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, ct).ConfigureAwait(false);
        if (user is { Status: UserStatus.Deactivated })
        {
            return Err(OAuthConstants.ErrorCodes.InvalidRequest, "User account has been deactivated.");
        }

        var subject = await pairwiseSubjectService.GetSubjectAsync(client, request.UserId, ct).ConfigureAwait(false);

        // ---------------- Access token (id_token token only) ----------------
        string? accessToken = null;
        int? expiresIn = null;
        if (includeAccessToken)
        {
            string audience;
            if (!string.IsNullOrWhiteSpace(valid.Resource))
            {
                audience = valid.Resource;
            }
            else
            {
                audience = ResolveConfiguredDefaultAudience(authOptions.Value.ApiAudiences) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(audience))
                {
                    return Err(OAuthConstants.ErrorCodes.InvalidTarget, "No access token audience could be resolved");
                }
            }

            var settings = await GetCurrentTenantSettingsSafeAsync(request.TenantId, ct).ConfigureAwait(false);
            var accessTokenLifetime = lifetimeResolver.ResolveAccessTokenLifetime(client, settings);
            expiresIn = (int)accessTokenLifetime.TotalSeconds;

            if (opaquePolicy.ShouldUseOpaqueAccessToken(audience))
            {
                var jti = Guid.NewGuid().ToString("N");
                var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var hash = CryptoHelper.ComputeSha256Base64(raw);
                db.Tokens.Add(new Persistence.Token
                {
                    Type = "access",
                    TokenHash = hash,
                    UserId = request.UserId,
                    ClientId = client.ClientId,
                    TenantId = request.TenantId,
                    ScopesJson = JsonSerializer.Serialize(scopes),
                    Audience = audience,
                    Jti = jti,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(accessTokenLifetime),
                    IpAddress = request.IpAddress,
                    UserAgent = request.UserAgent
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                accessToken = raw;
            }
            else
            {
                var roleNames = await LoadRoleNamesAsync(client, request.UserId, scopes, ct).ConfigureAwait(false);
                var claimRequest = new AccessTokenClaimRequest(
                    UserId: request.UserId,
                    ClientId: client.ClientId,
                    Scopes: scopes,
                    Issuer: request.Issuer,
                    UpstreamIdp: request.UpstreamIdp,
                    UpstreamAcr: request.UpstreamAcr,
                    CombinedAmr: request.UpstreamAmr,
                    TenantId: request.TenantId,
                    Subject: subject);
                var accessClaims = (await claimBuilder.BuildClaimsAsync(claimRequest, ct).ConfigureAwait(false)).ToList();
                var accessTokenJti = accessClaims.FirstOrDefault(c =>
                    string.Equals(c.Type, JwtRegisteredClaimNames.Jti, StringComparison.Ordinal))?.Value;

                accessToken = await jwt.CreateJwtAsync(
                    request.Issuer,
                    audience,
                    accessClaims,
                    DateTimeOffset.UtcNow.Add(accessTokenLifetime),
                    tokenType: SecurityConstants.JwtTokenTypes.AtJwt,
                    ct: ct).ConfigureAwait(false);

                db.Tokens.Add(new Persistence.Token
                {
                    Type = "access",
                    TokenHash = CryptoHelper.ComputeSha256Base64(accessToken),
                    UserId = request.UserId,
                    ClientId = client.ClientId,
                    TenantId = request.TenantId,
                    ScopesJson = JsonSerializer.Serialize(scopes),
                    Audience = audience,
                    Jti = accessTokenJti,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(accessTokenLifetime),
                    IpAddress = request.IpAddress,
                    UserAgent = request.UserAgent
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        // ---------------- ID token ----------------
        var activeKey = await keyProvider.GetActiveSigningKeyAsync(ct).ConfigureAwait(false);
        var idTokenSigningKey = activeKey;
        var signingAlg = GetJwaAlgOrDefault(activeKey);

        // A client registered a specific id_token_signed_response_alg: honour it or fail (no silent alg change).
        if (!string.IsNullOrWhiteSpace(client.IdTokenSignedResponseAlg))
        {
            if (string.Equals(client.IdTokenSignedResponseAlg, SecurityAlgorithms.None, StringComparison.OrdinalIgnoreCase))
            {
                return Err(OAuthConstants.ErrorCodes.InvalidRequest, "Client 'id_token_signed_response_alg' cannot be 'none'.");
            }

            if (!string.Equals(client.IdTokenSignedResponseAlg, signingAlg, StringComparison.Ordinal))
            {
                var compatibleJwkJson = await db.SigningKeys
                    .AsNoTracking()
                    .Where(k => k.TenantId == request.TenantId && k.Use == "sig" && k.RetiredAt == null && k.Alg == client.IdTokenSignedResponseAlg)
                    .OrderByDescending(k => k.CreatedAt)
                    .Select(k => k.JwkJson)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(compatibleJwkJson))
                {
                    return Err(OAuthConstants.ErrorCodes.InvalidRequest,
                        $"Client 'id_token_signed_response_alg' '{client.IdTokenSignedResponseAlg}' is not available for this tenant.");
                }

                compatibleJwkJson = secretProtector?.UnprotectSigningKeyJwk(compatibleJwkJson) ?? compatibleJwkJson;
                idTokenSigningKey = new JsonWebKey(compatibleJwkJson);
                signingAlg = GetJwaAlgOrDefault(idTokenSigningKey);
            }
        }

        var atHash = accessToken is null
            ? null
            : CryptoHelper.ComputeLeftHalfHashBase64Url(accessToken, signingAlg);

        var (requestedIdTokenClaims, _, essentialIdTokenClaims, _) = OidcClaimsRequestParser.ExtractRequestedClaimNames(valid.ClaimsJson);
        var (idTokenConstraints, _) = OidcClaimsRequestParser.ExtractClaimConstraints(valid.ClaimsJson);

        var profileGranted = scopes.Contains(OidcConstants.Scopes.Profile);
        var emailGranted = scopes.Contains(OidcConstants.Scopes.Email);
        var rolesGranted = scopes.Contains(OidcConstants.Scopes.Roles);

        var idClaims = new List<Claim> { new(OidcConstants.Claims.Subject, subject) };

        var emailRequested = requestedIdTokenClaims.Contains(OidcConstants.Claims.Email)
            || idTokenConstraints.ContainsKey(OidcConstants.Claims.Email);
        var emailVerifiedRequested = requestedIdTokenClaims.Contains(OidcConstants.Claims.EmailVerified)
            || idTokenConstraints.ContainsKey(OidcConstants.Claims.EmailVerified);
        var idpRequested = requestedIdTokenClaims.Contains(OidcConstants.Claims.Idp)
            || idTokenConstraints.ContainsKey(OidcConstants.Claims.Idp);

        if (user is not null)
        {
            if (profileGranted && !string.IsNullOrEmpty(user.Name))
                idClaims.Add(new(OidcConstants.Claims.Name, user.Name));
            if (emailGranted && emailRequested && !string.IsNullOrEmpty(user.Email))
                idClaims.Add(new(OidcConstants.Claims.Email, user.Email));
            if (emailGranted && emailVerifiedRequested)
                idClaims.Add(new(OidcConstants.Claims.EmailVerified, user.EmailVerified ? "true" : "false", ClaimValueTypes.Boolean));
            if (rolesGranted)
            {
                var roleNames = await LoadRoleNamesAsync(client, request.UserId, scopes, ct).ConfigureAwait(false);
                foreach (var r in roleNames) idClaims.Add(new(OidcConstants.Claims.Roles, r));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.UpstreamIdp) && idpRequested)
            idClaims.Add(new(OidcConstants.Claims.Idp, request.UpstreamIdp));
        if (!string.IsNullOrWhiteSpace(request.UpstreamAcr))
            idClaims.Add(new(OidcConstants.Claims.Acr, request.UpstreamAcr));
        if (authOptions.Value.EmitAmrInIdToken && request.UpstreamAmr is { Length: > 0 })
        {
            foreach (var amr in request.UpstreamAmr) idClaims.Add(new(OidcConstants.Claims.Amr, amr));
        }
        if (!string.IsNullOrWhiteSpace(request.SessionId))
            idClaims.Add(new(OidcConstants.Claims.Sid, request.SessionId));

        // Claims-parameter value constraints (OIDC Core §5.5.1): a claim whose value does not match the
        // requested constraint is omitted; only an essential acr that cannot be met fails the flow.
        DateTimeOffset? authTimeForIdToken = request.AuthTime;
        string? nonceForIdToken = valid.Nonce;
        string? atHashForIdToken = atHash;

        if (idTokenConstraints.Count > 0)
        {
            string? Single(string name) => name switch
            {
                OidcConstants.Claims.AuthTime when authTimeForIdToken is not null
                    => authTimeForIdToken.Value.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                OidcConstants.Claims.Nonce => nonceForIdToken,
                OidcConstants.Claims.AtHash => atHashForIdToken,
                _ => idClaims.FirstOrDefault(c => string.Equals(c.Type, name, StringComparison.Ordinal))?.Value,
            };

            IEnumerable<string> All(string name)
            {
                var v = Single(name);
                return v is null ? Array.Empty<string>() : new[] { v };
            }

            foreach (var kvp in idTokenConstraints)
            {
                var claimName = kvp.Key;
                var constraint = kvp.Value;
                if (constraint.Value is null && (constraint.Values is null || constraint.Values.Length == 0)) continue;

                var matches = constraint.Value is not null
                    ? All(claimName).Any(v => string.Equals(v, constraint.Value, StringComparison.Ordinal))
                    : All(claimName).Any(v => constraint.Values!.Contains(v, StringComparer.Ordinal));

                if (matches) continue;

                if (constraint.Essential && string.Equals(claimName, OidcConstants.Claims.Acr, StringComparison.Ordinal))
                {
                    return Err(OAuthConstants.ErrorCodes.InvalidRequest,
                        $"Essential id_token claim '{claimName}' cannot satisfy the requested value constraint.");
                }

                if (string.Equals(claimName, OidcConstants.Claims.AuthTime, StringComparison.Ordinal)) authTimeForIdToken = null;
                else if (string.Equals(claimName, OidcConstants.Claims.Nonce, StringComparison.Ordinal)) nonceForIdToken = null;
                else if (string.Equals(claimName, OidcConstants.Claims.AtHash, StringComparison.Ordinal)) atHashForIdToken = null;
                else idClaims.RemoveAll(c => string.Equals(c.Type, claimName, StringComparison.Ordinal));
            }
        }

        if (essentialIdTokenClaims.Count > 0 && logger.IsEnabled(LogLevel.Debug))
        {
            var present = idClaims.Select(c => c.Type).ToHashSet(StringComparer.Ordinal);
            foreach (var required in essentialIdTokenClaims)
            {
                var satisfied = present.Contains(required)
                    || (string.Equals(required, OidcConstants.Claims.AuthTime, StringComparison.Ordinal) && authTimeForIdToken is not null)
                    || (string.Equals(required, OidcConstants.Claims.Nonce, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(nonceForIdToken))
                    || (string.Equals(required, OidcConstants.Claims.AtHash, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(atHashForIdToken))
                    || string.Equals(required, JwtRegisteredClaimNames.Iat, StringComparison.Ordinal);
                if (!satisfied) logger.LogDebug("Essential id_token claim {Claim} not available; omitted", required);
            }
        }

        var settingsForIdToken = await GetCurrentTenantSettingsSafeAsync(request.TenantId, ct).ConfigureAwait(false);
        var idTokenLifetime = lifetimeResolver.ResolveIdentityTokenLifetime(client, settingsForIdToken);

        var idToken = await CreateIdTokenAsync(
            request.Issuer,
            client.ClientId,
            idClaims,
            DateTimeOffset.UtcNow.Add(idTokenLifetime),
            idTokenSigningKey,
            activeKey,
            nonceForIdToken,
            atHashForIdToken,
            authTimeForIdToken,
            ct).ConfigureAwait(false);

        // Fail closed on registered ID token encryption: never fall back to a plaintext ID token.
        var enc = await TryGetIdTokenEncryptingCredentialsAsync(client, ct).ConfigureAwait(false);
        if (enc is null && !string.IsNullOrWhiteSpace(client.IdTokenEncryptedResponseAlg))
        {
            logger.LogError("ID token encryption required but unavailable for client {ClientIdHash}", Bucketization.Bucket(client.ClientId));
            return Err(OAuthConstants.ErrorCodes.ServerError, "id_token encryption unavailable");
        }
        if (enc is not null)
        {
            idToken = await jwt.CreateJwtEncryptedAsync(
                request.Issuer,
                client.ClientId,
                idClaims,
                DateTimeOffset.UtcNow.Add(idTokenLifetime),
                enc,
                signingKey: idTokenSigningKey,
                nonce: nonceForIdToken,
                accessTokenHash: atHashForIdToken,
                authTime: authTimeForIdToken,
                ct: ct).ConfigureAwait(false);
        }

        return new ImplicitTokenResult(
            Ok: true,
            IdToken: idToken,
            AccessToken: accessToken,
            ExpiresInSeconds: expiresIn);
    }

    private async Task<string> CreateIdTokenAsync(
        string issuer,
        string audience,
        List<Claim> claims,
        DateTimeOffset expires,
        SecurityKey selectedKey,
        SecurityKey activeKey,
        string? nonce,
        string? atHash,
        DateTimeOffset? authTime,
        CancellationToken ct)
        => ReferenceEquals(selectedKey, activeKey)
            ? await jwt.CreateJwtAsync(issuer, audience, claims, expires, nonce: nonce, accessTokenHash: atHash, authTime: authTime, ct: ct).ConfigureAwait(false)
            : await jwt.CreateJwtAsync(issuer, audience, claims, expires, signingKey: selectedKey, nonce: nonce, accessTokenHash: atHash, authTime: authTime, ct: ct).ConfigureAwait(false);

    private async Task<EncryptingCredentials?> TryGetIdTokenEncryptingCredentialsAsync(Persistence.Client client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(client.IdTokenEncryptedResponseAlg) || string.IsNullOrWhiteSpace(client.IdTokenEncryptedResponseEnc)) return null;
        if (!string.Equals(client.IdTokenEncryptedResponseAlg, SecurityAlgorithms.RsaOAEP, StringComparison.Ordinal)
            || !string.Equals(client.IdTokenEncryptedResponseEnc, SecurityAlgorithms.Aes256CbcHmacSha512, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var key = await _clientJwksProvider.GetEncryptionKeyAsync(
                client,
                httpClientFactory,
                jwksCache,
                authOptions.Value.ClientJwksCacheSeconds,
                ct).ConfigureAwait(false);
            if (key is null || !string.Equals(key.Kty, "RSA", StringComparison.OrdinalIgnoreCase)) return null;
            return new EncryptingCredentials(key, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Resolving ID token encryption key failed for client {ClientIdHash}", Bucketization.Bucket(client.ClientId));
            return null;
        }
    }

    private async Task<string[]> LoadRoleNamesAsync(Persistence.Client client, Guid userId, string[] scopes, CancellationToken ct)
    {
        if (!scopes.Contains("roles", StringComparer.Ordinal)) return Array.Empty<string>();

        var realmRoles = db.UserRealmRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.RealmId == client.RealmId && a.IsActive)
            .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Where(x => x.r.IsActive)
            .Select(x => x.r.Name);

        var clientRoles = db.UserClientRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.ClientId == client.Id && a.IsActive)
            .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a, r })
            .Where(x => x.r.IsActive)
            .Select(x => x.r.Name);

        return await realmRoles.Union(clientRoles).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<TenantSettings> GetCurrentTenantSettingsSafeAsync(Guid tenantId, CancellationToken ct)
    {
        // Best-effort resolution with platform defaults as fallback: token lifetimes always resolve, even
        // when the tenant context is unavailable (e.g. direct unit-test construction).
        try
        {
            return await settingsService.GetTenantSettingsAsync(tenantId).ConfigureAwait(false)
                ?? settingsService.GetPlatformDefaults();
        }
        catch
        {
            return settingsService.GetPlatformDefaults();
        }
    }

    private static string? ResolveConfiguredDefaultAudience(string[]? configuredAudiences)
    {
        var allowed = configuredAudiences?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        return allowed.Length > 0 ? allowed[0] : null;
    }

    private static string GetJwaAlgOrDefault(SecurityKey key)
        => key is JsonWebKey jwk && !string.IsNullOrWhiteSpace(jwk.Alg) ? jwk.Alg : SecurityConstants.JwtAlgorithms.RS256;

    private static ImplicitTokenResult Err(string error, string description) => new(false, error, description);
}
