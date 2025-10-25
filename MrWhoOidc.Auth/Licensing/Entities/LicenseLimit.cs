using System;
using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Licensing.Entities;

public class LicenseLimit
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    [MaxLength(50)]
    public string Tier { get; set; } = string.Empty;

    [MaxLength(100)]
    public string LimitType { get; set; } = string.Empty;

    public long LimitValue { get; set; } = -1;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; set; }
}
