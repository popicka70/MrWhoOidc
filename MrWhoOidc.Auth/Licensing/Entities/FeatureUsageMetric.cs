using System;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Entities;

public class FeatureUsageMetric
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    public Guid? LicenseId { get; set; }

    public Guid? TenantId { get; set; }

    [MaxLength(100)]
    public string FeatureName { get; set; } = string.Empty;

    public long UsageCount { get; set; } = 1;

    public DateTimeOffset FirstUsed { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;

    public DateOnly AggregationDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public License? License { get; set; }

    public Tenant? Tenant { get; set; }
}
