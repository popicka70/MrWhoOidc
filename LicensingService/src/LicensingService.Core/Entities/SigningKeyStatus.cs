namespace LicensingService.Core.Entities;

/// <summary>
/// Status of a signing key.
/// </summary>
public enum SigningKeyStatus
{
    /// <summary>Key is currently active and used for signing new licenses.</summary>
    Active,

    /// <summary>Key has been rotated out but is still valid for verification.</summary>
    Rotated,

    /// <summary>Key has been retired and should no longer be used.</summary>
    Retired
}
