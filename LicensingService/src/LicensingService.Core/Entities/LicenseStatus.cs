namespace LicensingService.Core.Entities;

/// <summary>
/// Represents the status of a license in its lifecycle.
/// </summary>
public enum LicenseStatus
{
    /// <summary>License is currently valid and in use.</summary>
    Active,

    /// <summary>License has passed its expiration date.</summary>
    Expired,

    /// <summary>License has been manually revoked.</summary>
    Revoked,

    /// <summary>License has been superseded by a renewal.</summary>
    Renewed,

    /// <summary>License has been superseded by an upgrade.</summary>
    Upgraded,

    /// <summary>License has been superseded by a downgrade.</summary>
    Downgraded
}
