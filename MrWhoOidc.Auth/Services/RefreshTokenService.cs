using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for creating and managing refresh tokens.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Creates a new refresh token for a user and client.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="scopes">The granted scopes.</param>
    /// <param name="ipAddress">Optional IP address of the requester.</param>
    /// <param name="userAgent">Optional user agent of the requester.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="familyCreatedAt">Optional family origin timestamp carried during rotation.</param>
    /// <param name="cnfJkt">Optional DPoP key thumbprint to bind refresh token to.</param>
    /// <returns>A tuple containing the raw token and its hash.</returns>
    Task<(string token, string hash)> CreateRefreshTokenAsync(
        Guid userId,
        string clientId,
        string[] scopes,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default,
        DateTimeOffset? familyCreatedAt = null,
        string? cnfJkt = null);
}

internal sealed class RefreshTokenService(AuthDbContext db, ITenantAccessor tenantAccessor, ITenantSettingsService settingsService) : IRefreshTokenService
{
    // Default refresh token lifetimes.
    private const int DefaultRefreshTokenLifetimeSeconds = 15 * 24 * 60 * 60;          // 15 days (sliding)
    private const int DefaultRefreshTokenAbsoluteLifetimeSeconds = 30 * 24 * 60 * 60;  // 30 days (absolute cap)

    public async Task<(string token, string hash)> CreateRefreshTokenAsync(
        Guid userId,
        string clientId,
        string[] scopes,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default,
        DateTimeOffset? familyCreatedAt = null,
        string? cnfJkt = null)
    {
        // Get tenant-specific refresh token lifetime
        var tenantId = tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        var settings = await settingsService.GetTenantSettingsAsync(tenantId);
        var lifetimeSeconds = settings?.Tokens?.RefreshTokenLifetimeSeconds ?? DefaultRefreshTokenLifetimeSeconds;
        var absoluteLifetimeSeconds = settings?.Tokens?.RefreshTokenAbsoluteLifetimeSeconds ?? DefaultRefreshTokenAbsoluteLifetimeSeconds;

        // Guard against misconfiguration: lifetimes must be positive. Note that an
        // absolute lifetime shorter than the sliding lifetime is intentional and
        // supported — the effective expiry is min(sliding, absolute), so the absolute
        // window caps the sliding one.
        if (lifetimeSeconds <= 0)
        {
            lifetimeSeconds = DefaultRefreshTokenLifetimeSeconds;
        }
        if (absoluteLifetimeSeconds <= 0)
        {
            absoluteLifetimeSeconds = DefaultRefreshTokenAbsoluteLifetimeSeconds;
        }

        var lifetime = TimeSpan.FromSeconds(lifetimeSeconds);
        var absoluteLifetime = TimeSpan.FromSeconds(absoluteLifetimeSeconds);
        var now = DateTimeOffset.UtcNow;
        var createdAt = familyCreatedAt ?? now;

        var slidingExpiry = now.Add(lifetime);
        var absoluteExpiry = createdAt.Add(absoluteLifetime);
        var expiresAt = slidingExpiry <= absoluteExpiry ? slidingExpiry : absoluteExpiry;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = CryptoHelper.ComputeSha256Base64(token);
        db.Tokens.Add(new MrWhoOidc.Auth.Persistence.Token
        {
            Type = "refresh",
            TokenHash = hash,
            UserId = userId,
            ClientId = clientId,
            ScopesJson = System.Text.Json.JsonSerializer.Serialize(scopes),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            TenantId = tenantId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CnfJkt = cnfJkt,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (token, hash);
    }
}

