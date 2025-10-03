using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IQrLoginService
{
    Task<(string sessionToken, string authUrl)> CreateSessionAsync(
        string clientId, string returnUrl, string codeChallenge, 
        string codeChallengeMethod, string state, string? nonce, string scope);
    
    Task<QrLoginSession?> GetSessionAsync(string sessionToken);
    
    Task<QrLoginSession?> GetSessionByHashAsync(string sessionTokenHash);
    
    Task<bool> UpdateStatusAsync(string sessionToken, QrSessionStatus newStatus, 
        Guid? userId = null, string? authCode = null);
    
    Task<bool> MarkScannedAsync(string sessionToken, string? mobileIp, string? mobileUserAgent);
    
    Task ExpireSessionAsync(string sessionToken);
    
    Task<int> CleanupExpiredSessionsAsync(DateTimeOffset olderThan);
}

public sealed class QrLoginService : IQrLoginService
{
    private readonly AuthDbContext _db;
    private readonly IOptions<QrLoginOptions> _options;

    public QrLoginService(AuthDbContext db, IOptions<QrLoginOptions> options)
    {
        _db = db;
        _options = options;
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
        var hash = ComputeHash(sessionToken);

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
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(opts.SessionLifetimeSeconds)
        };

        _db.QrLoginSessions.Add(session);
        await _db.SaveChangesAsync();

        // Build authentication URL
        var baseUrl = opts.BaseUrl?.TrimEnd('/') ?? "https://localhost";
        var authUrl = $"{baseUrl}/Auth/QrMobile?session={Uri.EscapeDataString(sessionToken)}";

        return (sessionToken, authUrl);
    }

    public async Task<QrLoginSession?> GetSessionAsync(string sessionToken)
    {
        return await _db.QrLoginSessions
            .FirstOrDefaultAsync(s => s.SessionToken == sessionToken);
    }

    public async Task<QrLoginSession?> GetSessionByHashAsync(string sessionTokenHash)
    {
        return await _db.QrLoginSessions
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
        var expiredSessions = await _db.QrLoginSessions
            .Where(s => s.ExpiresAt < olderThan && 
                        (s.Status == QrSessionStatus.Expired || 
                         s.Status == QrSessionStatus.Cancelled ||
                         s.Status == QrSessionStatus.Consumed))
            .ToListAsync();

        _db.QrLoginSessions.RemoveRange(expiredSessions);
        await _db.SaveChangesAsync();

        return expiredSessions.Count;
    }

    private static string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
