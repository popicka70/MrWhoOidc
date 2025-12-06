namespace LicensingService.Core.Entities;

/// <summary>
/// Audit trail entry for license lifecycle events.
/// Events are append-only (never updated or deleted).
/// </summary>
public class LicenseEvent
{
    /// <summary>Unique identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>Reference to the license.</summary>
    public Guid LicenseId { get; set; }

    /// <summary>Type of event.</summary>
    public LicenseEventType EventType { get; set; }

    /// <summary>Event occurrence time.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>User who performed the action.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Event-specific details as JSON.</summary>
    public string? Details { get; set; }

    // Navigation properties
    /// <summary>The license this event belongs to.</summary>
    public License? License { get; set; }
}
