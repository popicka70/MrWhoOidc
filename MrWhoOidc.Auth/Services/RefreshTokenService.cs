using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IRefreshTokenService
{
    Task<(string token, string hash)> CreateRefreshTokenAsync(
        Guid userId, 
        string clientId, 
        TimeSpan lifetime, 
        string[] scopes, 
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default);
}

internal sealed class RefreshTokenService(AuthDbContext db, ITenantAccessor tenantAccessor) : IRefreshTokenService
{
    public async Task<(string token, string hash)> CreateRefreshTokenAsync(
        Guid userId, 
        string clientId, 
        TimeSpan lifetime, 
        string[] scopes, 
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Hash(token);
        db.Tokens.Add(new Token
        {
            Type = "refresh",
            TokenHash = hash,
            UserId = userId,
            ClientId = clientId,
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(scopes),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
            TenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required"),
            IpAddress = ipAddress,
            UserAgent = userAgent
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (token, hash);
    }

    static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
}

