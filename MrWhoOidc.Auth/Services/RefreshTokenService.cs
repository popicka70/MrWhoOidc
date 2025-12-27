using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

public interface IRefreshTokenService
{
    Task<(string token, string hash)> CreateRefreshTokenAsync(
        Guid userId,
        string clientId,
        string[] scopes,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default);
}

internal sealed class RefreshTokenService(AuthDbContext db, ITenantAccessor tenantAccessor, ITenantSettingsService settingsService) : IRefreshTokenService
{
    public async Task<(string token, string hash)> CreateRefreshTokenAsync(
        Guid userId,
        string clientId,
        string[] scopes,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default)
    {
        // Get tenant-specific refresh token lifetime
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        var settings = await settingsService.GetTenantSettingsAsync(tenantId);
        var lifetimeSeconds = settings?.Tokens?.RefreshTokenLifetimeSeconds ?? 1296000; // Default: 15 days
        var lifetime = TimeSpan.FromSeconds(lifetimeSeconds);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = CryptoHelper.ComputeSha256Base64(token);
        db.Tokens.Add(new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = hash,
            UserId = userId,
            ClientId = clientId,
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(scopes),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
            TenantId = tenantId,
            IpAddress = ipAddress,
            UserAgent = userAgent
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (token, hash);
    }
}

