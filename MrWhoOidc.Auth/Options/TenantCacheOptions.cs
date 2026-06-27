using System;

namespace MrWhoOidc.Auth.Options;

/// <summary>
/// Shared cache configuration for tenant-related operations.
/// Ensures consistent cache expiration times across all tenant services.
/// </summary>
public class TenantCacheOptions
{
    /// <summary>
    /// L2 cache (Redis) expiration time for tenant data.
    /// Default: 1 hour
    /// </summary>
    public TimeSpan L2Expiration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// L1 cache (in-memory) expiration time for tenant data.
    /// Default: 15 minutes
    /// </summary>
    public TimeSpan L1Expiration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Cache tag used for invalidation.
    /// </summary>
    public const string CacheTag = "tenants";
}
