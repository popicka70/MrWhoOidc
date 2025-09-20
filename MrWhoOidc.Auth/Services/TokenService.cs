using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

public interface ITokenService
{
    Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, CancellationToken ct = default);
    Task<(bool ok, object? payload, string? error, int status)> ExchangeRefreshTokenAsync(
        string refreshToken, string clientId, string issuer, CancellationToken ct = default);
}

internal sealed class TokenService(AuthDbContext db, IJwtService jwt, IRefreshTokenService refreshTokens, IOptions<AuthOptions> authOptions, IAuthorizationCodeMetadataStore meta) : ITokenService
{
    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, CancellationToken ct = default)
    {
        var entity = await db.AuthorizationCodes.FirstOrDefaultAsync(c => c.Code == code, ct).ConfigureAwait(false);
        if (entity is null || entity.Consumed || entity.ExpiresAt < DateTimeOffset.UtcNow)
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);

        if (!string.Equals(entity.RedirectUri, redirectUri, StringComparison.Ordinal))
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);

        if (!string.Equals(entity.ClientId, clientId, StringComparison.Ordinal))
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);

        // Validate PKCE S256
        if (!string.IsNullOrEmpty(entity.CodeChallenge))
        {
            var s256 = ComputeS256(codeVerifier);
            if (!string.Equals(s256, entity.CodeChallenge, StringComparison.Ordinal))
                return (false, new { error = "invalid_grant" }, "invalid_grant", 400);
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

        string accessToken;
        if (opaqueEnabled)
        {
            // Create opaque token (random 256-bit), persist with hash
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await PersistOpaqueAccessAsync(entity.UserId, clientId, audience, scopes, Guid.NewGuid().ToString("N"), raw, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
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
            accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.AddMinutes(15));
        }

        // Compute at_hash per OIDC (left-most half of SHA-256 of access token)
        var atHash = ComputeAtHash(accessToken);

        // Prepare ID token claims, include profile/email if requested
        var idClaims = new List<System.Security.Claims.Claim>
        {
            new("sub", entity.UserId.ToString())
        };

        // Load user once for optional claims
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == entity.UserId, ct).ConfigureAwait(false);
        if (user is not null)
        {
            if (scopes.Contains("profile") && !string.IsNullOrEmpty(user.Name))
                idClaims.Add(new("name", user.Name));
            if (scopes.Contains("email") && !string.IsNullOrEmpty(user.Email))
            {
                idClaims.Add(new("email", user.Email));
                idClaims.Add(new("email_verified", user.EmailVerified ? "true" : "false"));
            }
        }

        // Pull auth_time from metadata store if available
        DateTimeOffset? authTime = null;
        if (meta.TryGetAuthTime(code, out var at)) authTime = at;

        var idToken = jwt.CreateJwt(
            issuer,
            clientId,
            idClaims,
            DateTimeOffset.UtcNow.AddMinutes(5),
            nonce: entity.Nonce,
            accessTokenHash: atHash,
            authTime: authTime
        );

        var (refreshToken, _) = await refreshTokens.CreateRefreshTokenAsync(entity.UserId, clientId, TimeSpan.FromDays(30), scopes, ct).ConfigureAwait(false);

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
        string refreshToken, string clientId, string issuer, CancellationToken ct = default)
    {
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

        string accessToken;
        if (opaqueEnabled)
        {
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await PersistOpaqueAccessAsync(tokenEntity.UserId, clientId, audience, scopes, Guid.NewGuid().ToString("N"), raw, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
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
            accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.AddMinutes(15));
        }

        // Rotation: create new refresh token and revoke the old one
        var (newRefresh, _) = await refreshTokens.CreateRefreshTokenAsync(tokenEntity.UserId, clientId, TimeSpan.FromDays(30), scopes, ct).ConfigureAwait(false);
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

    async Task PersistOpaqueAccessAsync(Guid userId, string clientId, string audience, string[] scopes, string jti, string rawToken, TimeSpan lifetime, CancellationToken ct)
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
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime)
        };
        db.Tokens.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    static string ComputeS256(string verifier)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        // PKCE S256 is full base64url-encoded SHA-256 without padding
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    static string ComputeAtHash(string accessToken)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.ASCII.GetBytes(accessToken));
        var left = bytes.Take(16).ToArray();
        return Convert.ToBase64String(left).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
}
