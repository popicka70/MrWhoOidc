namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Legacy support-access audit record retained for existing database history.
/// New support access activity is written to TenantSupportAccessSession.
/// </summary>
public class ImpersonationAuditLog
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid PlatformAdminUserId { get; set; }
    public string PlatformAdminUsername { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public ImpersonationAction Action { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public Guid? StartLogId { get; set; }
    public TimeSpan? Duration { get; set; }
    public string? Notes { get; set; }
    public User? PlatformAdmin { get; set; }
    public Tenant? Tenant { get; set; }
}

public enum ImpersonationAction
{
    Start = 1,
    Stop = 2
}