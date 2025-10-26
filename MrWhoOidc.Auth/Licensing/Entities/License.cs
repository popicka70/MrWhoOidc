using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Entities;

public class License
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// Tenant scope. Null indicates a platform-wide license.
    /// </summary>
    public Guid? TenantId { get; set; }

    [MaxLength(2000)]
    public string LicenseKey { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Tier { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? OrganizationName { get; set; }

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset ValidUntil { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? RevokedAt { get; set; }

    [MaxLength(500)]
    public string? RevocationReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public Tenant? Tenant { get; set; }

    public User? CreatedByUser { get; set; }

    public User? UpdatedByUser { get; set; }

    public ICollection<LicenseHistoryEntry> History { get; set; } = new List<LicenseHistoryEntry>();

    public ICollection<FeatureUsageMetric> UsageMetrics { get; set; } = new List<FeatureUsageMetric>();
}
