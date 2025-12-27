using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Validators;

/// <summary>
/// Service for validating platform and tenant licenses.
/// </summary>
public interface ILicenseValidator
{
    /// <summary>
    /// Validates the cryptographic signature of a license key.
    /// </summary>
    /// <param name="licenseKey">The license key string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<LicenseValidationResult> ValidateSignatureAsync(string licenseKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a license key into a LicenseInfo object.
    /// </summary>
    /// <param name="licenseKey">The license key string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed license info, or null if parsing fails.</returns>
    Task<LicenseInfo?> ParseLicenseAsync(string licenseKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the business rules (expiry, features, etc.) of a license.
    /// </summary>
    /// <param name="licenseInfo">The license info to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<LicenseValidationResult> ValidateBusinessRulesAsync(LicenseInfo licenseInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a tenant sublicense is a valid subset of the platform license.
    /// Checks that features, limits, and expiry don't exceed the platform license.
    /// </summary>
    /// <param name="sublicense">The tenant sublicense to validate.</param>
    /// <param name="platformLicense">The platform license that the sublicense must be a subset of.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result indicating success or describing the constraint violation.</returns>
    Task<LicenseValidationResult> ValidateSublicenseAsync(
        LicenseInfo sublicense, 
        LicenseInfo platformLicense, 
        CancellationToken cancellationToken = default);
}
