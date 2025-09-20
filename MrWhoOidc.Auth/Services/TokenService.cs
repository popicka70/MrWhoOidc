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

internal sealed class TokenService(AuthDbContext db, IJwtService jwt, IRefreshTokenService refreshTokens, IOptions<AuthOptions> authOptions) : ITokenService
{
    public async Task<(bool ok, object? payload, string? error, int status)> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string clientId, string codeVerifier, string issuer, CancellationToken ct = default)
    {
        var entity = await db.AuthorizationCodes.FirstOrDefaultAsync(c => c.Code == code, ct);
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
        var audience = (authOptions.Value.ApiAudiences?.FirstOrDefault()) ?? "api";

        // Build access token first (include scopes claim)
        var accessClaims = new List<System.Security.Claims.Claim>
        {
            new("sub", entity.UserId.ToString()),
            new("scope", string.Join(' ', scopes))
        };
        var accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.AddMinutes(15));

        // Compute at_hash per OIDC (left-most half of SHA-256 of access token)
        var atHash = ComputeAtHash(accessToken);

        // Prepare ID token claims, include profile/email if requested
        var idClaims = new List<System.Security.Claims.Claim>
        {
            new("sub", entity.UserId.ToString()),
            new("aud", clientId)
        };

        // Load user once for optional claims
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == entity.UserId, ct);
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

        var idToken = jwt.CreateJwt(
            issuer,
            clientId,
            idClaims,
            DateTimeOffset.UtcNow.AddMinutes(5),
            nonce: entity.Nonce,
            accessTokenHash: atHash,
            authTime: DateTimeOffset.UtcNow // TODO: load actual auth_time from session
        );

        var (refreshToken, _) = await refreshTokens.CreateRefreshTokenAsync(entity.UserId, clientId, TimeSpan.FromDays(30), scopes, ct);

        entity.Consumed = true;
        await db.SaveChangesAsync(ct);

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
        var tokenEntity = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == "refresh" && t.RevokedAt == null, ct);
        if (tokenEntity is null || tokenEntity.ExpiresAt < DateTimeOffset.UtcNow || !string.Equals(tokenEntity.ClientId, clientId, StringComparison.Ordinal))
            return (false, new { error = "invalid_grant" }, "invalid_grant", 400);

        var scopes = JsonSerializer.Deserialize<string[]>(tokenEntity.ScopesJson) ?? Array.Empty<string>();
        var audience = (authOptions.Value.ApiAudiences?.FirstOrDefault()) ?? "api";

        var accessClaims = new List<System.Security.Claims.Claim>
        {
            new("sub", tokenEntity.UserId.ToString()),
            new("scope", string.Join(' ', scopes))
        };
        var accessToken = jwt.CreateJwt(issuer, audience, accessClaims, DateTimeOffset.UtcNow.AddMinutes(15));

        // Rotation: create new refresh token and revoke the old one
        var (newRefresh, _) = await refreshTokens.CreateRefreshTokenAsync(tokenEntity.UserId, clientId, TimeSpan.FromDays(30), scopes, ct);
        tokenEntity.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

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

    static string ComputeS256(string verifier)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
