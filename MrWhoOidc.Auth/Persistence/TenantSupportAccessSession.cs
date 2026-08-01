using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Durable, bounded record authorizing one platform administrator to access one tenant for support.
/// Replaces the legacy ImpersonationAuditLog pattern with a full lifecycle session model.
/// </summary>
public class TenantSupportAccessSession
{
    /// <summary>
    /// Unique identifier for this support access session (UUID primary key).
    /// </summary>
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The platform administrator who initiated this support access session.
    /// </summary>
    public Guid PlatformAdminUserAccountId { get; set; }

    /// <summary>
    /// The tenant being accessed for support purposes.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// The mode of support access (initially ReadOnly; enum permits future controlled modes).
    /// </summary>
    public SupportAccessMode Mode { get; set; } = SupportAccessMode.ReadOnly;

    /// <summary>
    /// Current lifecycle status of this support session.
    /// </summary>
    public SupportAccessStatus Status { get; set; } = SupportAccessStatus.Active;

    /// <summary>
    /// Required bounded text describing the reason for support access.
    /// </summary>
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional bounded text referencing a support ticket or external tracking identifier.
    /// </summary>
    [MaxLength(100)]
    public string? TicketReference { get; set; }

    /// <summary>
    /// When this support session was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Absolute expiry time for this session. Must be set on creation.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When this session was ended by the platform administrator (if ended gracefully).
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// When this session was revoked by another authorized platform administrator.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The platform administrator who revoked this session.
    /// </summary>
    public Guid? RevokedByUserAccountId { get; set; }

    /// <summary>
    /// Reason for revocation (provided by the revoking administrator).
    /// </summary>
    [MaxLength(500)]
    public string? RevocationReason { get; set; }

    /// <summary>
    /// When the session was last used (updated on successful authorization).
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Hash of the IP address that created this session (PII-safe).
    /// </summary>
    [MaxLength(64)]
    public string? CreatedFromIpHash { get; set; }

    /// <summary>
    /// Hash of the user agent that created this session (PII-safe).
    /// </summary>
    [MaxLength(64)]
    public string? UserAgentHash { get; set; }

    /// <summary>
    /// Concurrency token for optimistic concurrency control. Updated on every write.
    /// </summary>
    public Guid ConcurrencyToken { get; set; } = GuidHelper.NewId();
}

/// <summary>
/// Modes of support access. ReadOnly is the initial and default mode.
/// Future releases may add controlled modes as policy permits.
/// </summary>
public enum SupportAccessMode
{
    /// <summary>
    /// Read-only support access. Only read operations are permitted.
    /// </summary>
    ReadOnly = 0
}

/// <summary>
/// Lifecycle status for a support access session.
/// </summary>
public enum SupportAccessStatus
{
    /// <summary>
    /// Session is active and may be used for authorized operations.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Session was ended gracefully by the platform administrator.
    /// </summary>
    Ended = 1,

    /// <summary>
    /// Session expired without explicit end or revocation.
    /// </summary>
    Expired = 2,

    /// <summary>
    /// Session was revoked by another authorized platform administrator.
    /// </summary>
    Revoked = 3
}
