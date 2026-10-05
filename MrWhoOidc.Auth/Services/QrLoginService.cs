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
    /// Creates a new QR login session bound to the initiating browser.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="returnUrl">The return URL.</param>
    /// <param name="codeChallenge">The PKCE code challenge.</param>
    /// <param name="codeChallengeMethod">The PKCE code challenge method.</param>
    /// <param name="state">The state parameter.</param>
    /// <param name="nonce">The nonce parameter.</param>
    /// <param name="scope">The requested scopes.</param>
    /// <param name="initiator">IP address and user agent of the browser that starts the login (shown on the phone).</param>
    /// <returns>
    /// The session token, the mobile URL encoded in the QR code, the initiator secret (to be put in the initiating
    /// browser's cookie; only its hash is stored) and the number the phone user must type to confirm.
    /// </returns>
    Task<QrSessionCreated> CreateSessionAsync(
        string clientId, string returnUrl, string codeChallenge,
        string codeChallengeMethod, string state, string? nonce, string scope,
        QrInitiatorInfo initiator);

    /// <summary>
    /// Builds the mobile landing URL encoded in the QR code for a session, on this deployment's own origin.
    /// </summary>
    string BuildMobileUrl(string sessionToken);

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

    public async Task<QrSessionCreated> CreateSessionAsync(
        string clientId, string returnUrl, string codeChallenge,
        string codeChallengeMethod, string state, string? nonce, string scope,
        QrInitiatorInfo initiator)
    {
        var opts = _options.Value;

        // Generate secure session token (32 bytes = 256 bits)
        var sessionToken = Base64Url(RandomNumberGenerator.GetBytes(32));

        // Compute hash for database lookup
        var hash = CryptoHelper.ComputeSha256Hex(sessionToken);

        // H9: the session token travels in the QR code, so whoever scans (or is sent) it knows it. The secret below
        // never leaves the initiating browser (HttpOnly cookie); only its hash is stored. The match code is shown on
        // the initiating screen only and must be typed on the phone.
        var initiatorSecret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var matchCode = RandomNumberGenerator.GetInt32(0, 100).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

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
            InitiatorSecretHash = CryptoHelper.ComputeSha256Hex(initiatorSecret),
            MatchCode = matchCode,
            InitiatorIpAddress = Truncate(initiator.IpAddress, 100),
            InitiatorUserAgent = Truncate(initiator.UserAgent, 500),
            TenantId = _tenantAccessor.CurrentTenant?.TenantId ?? throw new InvalidOperationException("Tenant context required")
        };

        _db.QrLoginSessions.Add(session);
        await _db.SaveChangesAsync();

        return new QrSessionCreated(sessionToken, BuildMobileUrl(sessionToken), initiatorSecret, matchCode, session.ExpiresAt);
    }

    public string BuildMobileUrl(string sessionToken)
    {
        // Prefer an explicitly-configured BaseUrl; otherwise derive it from the current tenant's issuer so the QR
        // encodes THIS deployment's own origin. Falling back to a hard-coded or third-party host would ship QR codes
        // that send users' session tokens off-origin to a host the operator does not control.
        var baseUrl = ResolveQrBaseUrl(_options.Value.BaseUrl);
        return $"{baseUrl}/auth/qr-mobile?session={Uri.EscapeDataString(sessionToken)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length > max ? value[..max] : value;

    private string ResolveQrBaseUrl(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var issuer = _tenantAccessor.CurrentTenant?.IssuerUri;
        if (!string.IsNullOrWhiteSpace(issuer) && Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
        {
            // Use only the authority (scheme + host + port); the issuer may include a tenant path.
            return issuerUri.GetLeftPart(UriPartial.Authority);
        }

        throw new InvalidOperationException(
            "QrLogin:BaseUrl is not configured and the tenant issuer is unavailable; cannot build a QR login URL on the deployment's own origin.");
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

/// <summary>Where a QR login was started from; shown to the confirming user so they can spot a request they did not make.</summary>
public sealed record QrInitiatorInfo(string? IpAddress, string? UserAgent);

/// <summary>Result of creating a QR login session.</summary>
/// <param name="SessionToken">Token identifying the session; it is encoded in the QR code, so it is not a secret.</param>
/// <param name="MobileUrl">The URL encoded in the QR code.</param>
/// <param name="InitiatorSecret">Secret for the initiating browser's binding cookie. Only its hash is persisted.</param>
/// <param name="MatchCode">Number to display on the initiating screen only.</param>
/// <param name="ExpiresAt">When the session expires.</param>
public sealed record QrSessionCreated(string SessionToken, string MobileUrl, string InitiatorSecret, string MatchCode, DateTimeOffset ExpiresAt);
