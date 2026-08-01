using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Durable authorization created by one user (delegator) and accepted by another user (delegate),
/// allowing constrained actions on behalf of the delegator within one tenant.
/// Implements AD-2: Bind grants to global accounts and one tenant.
/// </summary>
public class DelegatedAccessGrant
{
    /// <summary>
    /// Unique identifier for this delegated access grant (UUID primary key).
    /// </summary>
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The tenant that both delegator and delegate must have active memberships in.
    /// </summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// The tenant-scoped OAuth/OIDC client in which this grant may be exercised.
    /// </summary>
    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>
    /// The user granting authority over their own resources or actions.
    /// </summary>
    public Guid DelegatorUserAccountId { get; set; }
    public UserAccount DelegatorUserAccount { get; set; } = null!;

    /// <summary>
    /// The user receiving and exercising delegated authority.
    /// </summary>
    public Guid DelegateUserAccountId { get; set; }
    public UserAccount DelegateUserAccount { get; set; } = null!;

    /// <summary>
    /// Current lifecycle status of this delegated access grant.
    /// </summary>
    public DelegatedAccessGrantStatus Status { get; set; } = DelegatedAccessGrantStatus.PendingAcceptance;

    /// <summary>
    /// Bounded canonical JSON array of delegated capabilities.
    /// Must be non-empty and contain only registered delegable capability names.
    /// </summary>
    [MaxLength(8000)]
    public string CapabilitiesJson { get; set; } = "[]";

    /// <summary>
    /// Bounded validated JSON object defining resource constraints per capability.
    /// </summary>
    [MaxLength(8000)]
    public string ResourceConstraintsJson { get; set; } = "{}";

    /// <summary>
    /// Required bounded text describing the purpose of this delegated access grant.
    /// </summary>
    [MaxLength(500)]
    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// When this grant was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Deadline by which the delegate must accept or decline the grant invitation.
    /// Must be <= ExpiresAt.
    /// </summary>
    public DateTimeOffset AcceptanceExpiresAt { get; set; }

    /// <summary>
    /// When the delegate accepted this grant (null if not yet accepted).
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>
    /// When the grant becomes active (null for PendingAcceptance grants).
    /// Typically set to the acceptance timestamp or a delegator-specified start time.
    /// </summary>
    public DateTimeOffset? StartsAt { get; set; }

    /// <summary>
    /// Absolute expiry time for this grant. Must be > CreatedAt.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the delegate declined this grant (only for Declined status).
    /// </summary>
    public DateTimeOffset? DeclinedAt { get; set; }

    /// <summary>
    /// When this grant was revoked by either party or an authorized administrator.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The user account that revoked this grant.
    /// </summary>
    public Guid? RevokedByUserAccountId { get; set; }
    public UserAccount? RevokedByUserAccount { get; set; }

    /// <summary>
    /// Reason for revocation (provided by the revoking party).
    /// </summary>
    [MaxLength(500)]
    public string? RevocationReason { get; set; }

    /// <summary>
    /// When this grant was last used for an authorized delegated operation.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// Cumulative count of authorized uses of this grant.
    /// </summary>
    public long UseCount { get; set; } = 0;

    /// <summary>
    /// Concurrency token for optimistic concurrency control. Updated on every write.
    /// </summary>
    public Guid Version { get; set; } = GuidHelper.NewId();
}

/// <summary>
/// Lifecycle status for a delegated access grant.
/// Terminal states (Declined, Revoked, Expired) cannot become active again.
/// Implements AD-5: Use durable records with immediate revocation.
/// </summary>
public enum DelegatedAccessGrantStatus
{
    /// <summary>
    /// Grant has been created by delegator but not yet accepted by delegate.
    /// </summary>
    PendingAcceptance = 0,

    /// <summary>
    /// Grant has been accepted by delegate and is active for authorized operations.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Delegate explicitly declined the grant invitation.
    /// Terminal state — cannot become active.
    /// </summary>
    Declined = 2,

    /// <summary>
    /// Grant is temporarily suspended due to membership or risk policy.
    /// May be restored to Active by policy.
    /// </summary>
    Suspended = 3,

    /// <summary>
    /// Grant was revoked by delegator, delegate, or authorized administrator.
    /// Terminal state — cannot become active.
    /// </summary>
    Revoked = 4,

    /// <summary>
    /// Grant expired without explicit acceptance or revocation.
    /// Terminal state — cannot become active.
    /// </summary>
    Expired = 5
}
