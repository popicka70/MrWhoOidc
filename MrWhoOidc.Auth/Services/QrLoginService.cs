using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for managing QR-code based login sessions.
/// </summary>
public interface IQrLoginService
{
    /// <summary>
    /// Creates a new QR login session.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="returnUrl">The return URL.</param>
    /// <param name="codeChallenge">The PKCE code challenge.</param>
    /// <param name="codeChallengeMethod">The PKCE code challenge method.</param>
    /// <param name="state">The state parameter.</param>
    /// <param name="nonce">The nonce parameter.</param>
    /// <param name="scope">The requested scopes.</param>
    /// <returns>A tuple containing the session token and the authentication URL.</returns>
    Task<(string sessionToken, string authUrl)> CreateSessionAsync(
        string clientId, string returnUrl, string codeChallenge,
        string codeChallengeMethod, string state, string? nonce, string scope);

    /// <summary>
    /// Retrieves a session by its token.
    /// </summary>
    /// <param name="sessionToken">The session token.</param>
    /// <returns>The session if found; otherwise, null.</returns>
    Task<QrLoginSession?> GetSessionAsync(string sessionToken);

    /// <summary>
    /// Retrieves a session by its token hash.
    /// </summary>
    /// <param name="sessionTokenHash">The session token hash.</param>
    /// <returns>The session if found; otherwise, null.</returns>
    Task<QrLoginSession?> GetSessionByHashAsync(string sessionTokenHash);

    /// <summary>
    /// Updates the status of a session.
    /// </summary>
    /// <param name="sessionToken">The session token.</param>
    /// <param name="newStatus">The new status.</param>
    /// <param name="userId">Optional user ID.</param>
    /// <param name="authCode">Optional authorization code.</param>
    /// <returns>True if the update was successful; otherwise, false.</returns>
    Task<bool> UpdateStatusAsync(string sessionToken, QrSessionStatus newStatus,
        Guid? userId = null, string? authCode = null);

    /// <summary>
    /// Marks a session as scanned by a mobile device.
    /// </summary>
    /// <param name="sessionToken">The session token.</param>
    /// <param name="mobileIp">The IP address of the mobile device.</param>
    /// <param name="mobileUserAgent">The user agent of the mobile device.</param>
    /// <returns>True if the update was successful; otherwise, false.</returns>
    Task<bool> MarkScannedAsync(string sessionToken, string? mobileIp, string? mobileUserAgent);

    /// <summary>
    /// Expires a session immediately.
    /// </summary>
    /// <param name="sessionToken">The session token.</param>
    Task ExpireSessionAsync(string sessionToken);

    /// <summary>
    /// Cleans up expired sessions.
    /// </summary>
    /// <param name="olderThan">The cutoff time for expiration.</param>
    /// <returns>The number of sessions removed.</returns>
    Task<int> CleanupExpiredSessionsAsync(DateTimeOffset olderThan);
}

public sealed class QrLoginService : IQrLoginService
{
    private readonly AuthDbContext _db;
    private readonly IOptions<QrLoginOptions> _options;
    private readonly ITenantAccessor _tenantAccessor;

    public QrLoginService(AuthDbContext db, IOptions<QrLoginOptions> options, ITenantAccessor tenantAccessor)
    {
        _db = db;
        _options = options;
        _tenantAccessor = tenantAccessor;
    }

    public async Task<(string sessionToken, string authUrl)> CreateSessionAsync(
        string clientId, string returnUrl, string codeChallenge,
        string codeChallengeMethod, string state, string? nonce, string scope)
    {
        var opts = _options.Value;

        // Generate secure session token (32 bytes = 256 bits)
        var tokenBytes = new byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        var sessionToken = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        // Compute hash for database lookup
        var hash = CryptoHelper.ComputeSha256Hex(sessionToken);

        var session = new QrLoginSession
        {
            SessionToken = sessionToken,
            SessionTokenHash = hash,
            ClientId = clientId,
            ReturnUrl = returnUrl,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            State = state,
            Nonce = nonce,
            Scope = scope,
            Status = QrSessionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(opts.SessionLifetimeSeconds),
            TenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required")
        };

        _db.QrLoginSessions.Add(session);
        await _db.SaveChangesAsync();

        // Build authentication URL
        var baseUrl = opts.BaseUrl?.TrimEnd('/') ?? "https://localhost";
        var authUrl = $"{baseUrl}/auth/qr-mobile?session={Uri.EscapeDataString(sessionToken)}";

        return (sessionToken, authUrl);
    }

    public async Task<QrLoginSession?> GetSessionAsync(string sessionToken)
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        return await _db.QrLoginSessions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync(s => s.SessionToken == sessionToken);
    }

    public async Task<QrLoginSession?> GetSessionByHashAsync(string sessionTokenHash)
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");
        return await _db.QrLoginSessions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync(s => s.SessionTokenHash == sessionTokenHash);
    }

    public async Task<bool> UpdateStatusAsync(string sessionToken, QrSessionStatus newStatus,
        Guid? userId = null, string? authCode = null)
    {
        var session = await GetSessionAsync(sessionToken);
        if (session is null || session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return false;
        }

        session.Status = newStatus;

        if (userId.HasValue)
        {
            session.UserId = userId.Value;
        }

        if (!string.IsNullOrEmpty(authCode))
        {
            session.AuthorizationCode = authCode;
        }

        if (newStatus == QrSessionStatus.Authenticated)
        {
            session.AuthenticatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MarkScannedAsync(string sessionToken, string? mobileIp, string? mobileUserAgent)
    {
        var session = await GetSessionAsync(sessionToken);
        if (session is null || session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return false;
        }

        // Only update if not already scanned (unless AllowMultipleScans is true)
        if (session.Status == QrSessionStatus.Pending || _options.Value.AllowMultipleScans)
        {
            session.Status = QrSessionStatus.Scanned;
            session.ScannedAt = DateTimeOffset.UtcNow;
            session.MobileIpAddress = mobileIp?.Length > 100 ? mobileIp[..100] : mobileIp;
            session.MobileUserAgent = mobileUserAgent?.Length > 500 ? mobileUserAgent[..500] : mobileUserAgent;

            await _db.SaveChangesAsync();
            return true;
        }

        return false;
    }

    public async Task ExpireSessionAsync(string sessionToken)
    {
        var session = await GetSessionAsync(sessionToken);
        if (session is not null && session.Status != QrSessionStatus.Consumed)
        {
            session.Status = QrSessionStatus.Expired;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<int> CleanupExpiredSessionsAsync(DateTimeOffset olderThan)
    {
        var tenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required");

        // ⚡ Bolt Performance Optimization:
        // Replaced .ToListAsync() + .RemoveRange() with .ExecuteDeleteAsync()
        // Impact: Completely eliminates fetching large amounts of expired sessions into memory before deletion.
        var expiredCount = await _db.QrLoginSessions
            .Where(s => s.TenantId == tenantId && s.ExpiresAt < olderThan &&
                        (s.Status == QrSessionStatus.Expired ||
                         s.Status == QrSessionStatus.Cancelled ||
                         s.Status == QrSessionStatus.Consumed))
            .ExecuteDeleteAsync();

        return expiredCount;
    }
}
