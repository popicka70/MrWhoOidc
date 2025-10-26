using System;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Entities;

public class LicenseHistoryEntry
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    public Guid LicenseId { get; set; }

    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? OldLicenseKey { get; set; }

    [MaxLength(2000)]
    public string? NewLicenseKey { get; set; }

    [MaxLength(50)]
    public string? OldTier { get; set; }

    [MaxLength(50)]
    public string? NewTier { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedBy { get; set; }

    [MaxLength(200)]
    public string? UserAgent { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    public License License { get; set; } = null!;

    public User? CreatedByUser { get; set; }
}
