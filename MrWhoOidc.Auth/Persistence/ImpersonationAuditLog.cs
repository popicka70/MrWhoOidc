namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Audit log entry for impersonation events.
/// Tracks when platform admins start/stop impersonating tenants for security and compliance.
/// </summary>
public class ImpersonationAuditLog
{
    /// <summary>
    /// Unique identifier for this audit log entry.
    /// </summary>
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The platform admin user who performed the impersonation.
    /// </summary>
    public Guid PlatformAdminUserId { get; set; }

    /// <summary>
    /// Username of the platform admin (denormalized for quick lookup).
    /// </summary>
    public string PlatformAdminUsername { get; set; } = string.Empty;

    /// <summary>
    /// The tenant that was impersonated.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Tenant name (denormalized for quick lookup).
    /// </summary>
    public string TenantName { get; set; } = string.Empty;

    /// <summary>
    /// Tenant slug (denormalized for quick lookup).
    /// </summary>
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>
    /// Type of action: Start or Stop.
    /// </summary>
    public ImpersonationAction Action { get; set; }

    /// <summary>
    /// Timestamp when the action occurred (UTC).
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// IP address from which the action was performed.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User-Agent header for browser/device identification.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// For Stop actions: ID of the corresponding Start log entry.
    /// Used to correlate start/stop pairs and calculate duration.
    /// </summary>
    public Guid? StartLogId { get; set; }

    /// <summary>
    /// Duration of impersonation session (calculated on stop).
    /// Null for Start actions or if stop didn't correlate with a start.
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Additional context or notes (optional).
    /// Could include reason for impersonation, ticket numbers, etc.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Navigation property to the platform admin user.
    /// </summary>
    public User? PlatformAdmin { get; set; }

    /// <summary>
    /// Navigation property to the tenant.
    /// </summary>
    public Tenant? Tenant { get; set; }
}

/// <summary>
/// Types of impersonation actions that can be logged.
/// </summary>
public enum ImpersonationAction
{
    /// <summary>
    /// Admin started impersonating a tenant.
    /// </summary>
    Start = 1,

    /// <summary>
    /// Admin stopped impersonating a tenant.
    /// </summary>
    Stop = 2
}
