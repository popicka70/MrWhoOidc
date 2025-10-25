using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Licensing.Validators;

public interface ILicenseValidator
{
    Task<LicenseValidationResult> ValidateSignatureAsync(string licenseKey, CancellationToken cancellationToken = default);

    Task<LicenseInfo?> ParseLicenseAsync(string licenseKey, CancellationToken cancellationToken = default);

    Task<LicenseValidationResult> ValidateBusinessRulesAsync(LicenseInfo licenseInfo, CancellationToken cancellationToken = default);
}
