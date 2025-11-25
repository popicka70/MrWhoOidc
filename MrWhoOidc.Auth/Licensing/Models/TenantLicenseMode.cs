namespace MrWhoOidc.Auth.Licensing.Models;

/// <summary>
/// Determines how a tenant's effective license is resolved.
/// </summary>
public enum TenantLicenseMode
{
    /// <summary>
    /// Tenant inherits all features from the platform license.
    /// No tenant-specific license is required.
    /// </summary>
    InheritPlatform = 0,

    /// <summary>
    /// Tenant has its own sublicense which must be a subset of platform license features.
    /// Features, limits, and expiry cannot exceed platform license.
    /// </summary>
    Sublicense = 1
}
