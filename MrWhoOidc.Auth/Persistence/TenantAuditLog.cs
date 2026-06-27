using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// L4: Audit trail for tenant changes.
/// Records creation, updates, and deletion of tenant entities.
/// </summary>
public class TenantAuditLog
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The tenant being audited.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// The action performed (Created, Updated, Deleted, Suspended, Reactivated).
    /// </summary>
    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// The user who performed the action.
    /// </summary>
    [MaxLength(200)]
    public string? PerformedBy { get; set; }

    /// <summary>
    /// JSON payload with before/after state changes.
    /// </summary>
    public string? ChangesJson { get; set; }

    /// <summary>
    /// UTC timestamp when the action occurred.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
