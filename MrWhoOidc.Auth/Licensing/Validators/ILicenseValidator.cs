using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Validators;

public interface ILicenseValidator
{
    Task<LicenseValidationResult> ValidateSignatureAsync(string licenseKey, CancellationToken cancellationToken = default);

    Task<LicenseInfo?> ParseLicenseAsync(string licenseKey, CancellationToken cancellationToken = default);

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
