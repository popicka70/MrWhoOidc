namespace LicensingService.Core.Entities;

/// <summary>
/// Types of license lifecycle events for the audit trail.
/// </summary>
public enum LicenseEventType
{
    /// <summary>License was created.</summary>
    Created,

    /// <summary>License was renewed.</summary>
    Renewed,

    /// <summary>License was revoked.</summary>
    Revoked,

    /// <summary>License tier was upgraded.</summary>
    Upgraded,

    /// <summary>License tier was downgraded.</summary>
    Downgraded,

    /// <summary>License was validated via API.</summary>
    Validated
}
