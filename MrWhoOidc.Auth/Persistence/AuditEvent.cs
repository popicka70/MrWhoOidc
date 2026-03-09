using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Generic security audit event emitted by WebAuth handlers and background workers.
/// Payload is stored as JSON for flexible, append-only auditing.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// Tenant scope for the event. Null when tenant context is unavailable.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Event type identifier (for example: bcl.success, logout.redirect.rejected_not_allowed).
    /// </summary>
    [MaxLength(120)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Serialized event payload (JSON object).
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// UTC timestamp when the event was emitted.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Correlation/trace identifier when available.
    /// </summary>
    [MaxLength(128)]
    public string? TraceId { get; set; }

    /// <summary>
    /// Hash of actor subject identifier when available.
    /// </summary>
    [MaxLength(128)]
    public string? ActorHash { get; set; }

    /// <summary>
    /// Hash of source IP address when available.
    /// </summary>
    [MaxLength(128)]
    public string? IpHash { get; set; }
}
