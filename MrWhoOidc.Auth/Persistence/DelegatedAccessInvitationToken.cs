using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Single-use invitation token for delegated access grant acceptance.
/// Tokens are hashed at rest (SHA-256) and redeemed atomically.
/// Implements AD-5: Use durable records with immediate revocation.
/// </summary>
public class DelegatedAccessInvitationToken
{
    /// <summary>
    /// Unique identifier for this invitation token (UUID primary key).
    /// </summary>
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The tenant that both delegator and delegate must have active memberships in.
    /// Derived from the associated DelegatedAccessGrant.
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// The delegated access grant this invitation token is associated with.
    /// </summary>
    public Guid GrantId { get; set; }
    public DelegatedAccessGrant Grant { get; set; } = null!;

    /// <summary>
    /// SHA-256 hash of the raw invitation token sent to the delegate.
    /// Only the hash is stored; raw tokens are never persisted.
    /// </summary>
    [MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// When this invitation token was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this invitation token expires (must match or be before grant's AcceptanceExpiresAt).
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When this invitation token was consumed (used for acceptance).
    /// Null if not yet consumed.
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>
    /// When this invitation token was revoked (grant revoked before consumption).
    /// Null if not revoked.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
