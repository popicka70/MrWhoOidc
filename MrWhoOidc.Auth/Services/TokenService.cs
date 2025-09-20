using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
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

internal sealed class TokenService(AuthDbContext db, IJwtService jwt, IRefreshTokenService refreshTokens) : ITokenService
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

        var idClaims = new List<System.Security.Claims.Claim>
        {
            new("sub", entity.UserId.ToString()),
            new("aud", clientId)
        };

        var idToken = jwt.CreateJwt(
            issuer,
            clientId,
            idClaims,
            DateTimeOffset.UtcNow.AddMinutes(5),
            nonce: entity.Nonce,
            accessTokenHash: null,
            authTime: DateTimeOffset.UtcNow // TODO: track real auth_time from login
        );

        var accessClaims = new [] { new System.Security.Claims.Claim("sub", entity.UserId.ToString()) };
        var accessToken = jwt.CreateJwt(issuer, "api", accessClaims, DateTimeOffset.UtcNow.AddMinutes(15));

        // Refresh token issuance & rotation baseline
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

        var accessToken = jwt.CreateJwt(issuer, "api", new [] { new System.Security.Claims.Claim("sub", tokenEntity.UserId.ToString()) }, DateTimeOffset.UtcNow.AddMinutes(15));

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

    static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
}
