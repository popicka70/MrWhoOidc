namespace MrWhoOidc.Auth.Licensing.Models;

/// <summary>
/// Indicates the scope a license applies to.
/// </summary>
public enum LicenseScope
{
    /// <summary>
    /// License applies to the entire platform (no tenant binding).
    /// </summary>
    Platform = 0,

    /// <summary>
    /// License applies to a specific tenant.
    /// </summary>
    Tenant = 1
}
