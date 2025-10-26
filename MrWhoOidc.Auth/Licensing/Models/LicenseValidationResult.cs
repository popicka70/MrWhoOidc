namespace MrWhoOidc.Auth.Licensing.Models;

public sealed record LicenseValidationResult(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage,
    LicenseInfo? LicenseInfo)
{
    public static LicenseValidationResult Success(LicenseInfo license)
    {
        ArgumentNullException.ThrowIfNull(license);
        return new LicenseValidationResult(true, null, null, license);
    }

    public static LicenseValidationResult Failure(string errorCode, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new LicenseValidationResult(false, errorCode, errorMessage, null);
    }

    public static LicenseValidationResult InvalidSignature() => Failure("invalid_signature", "License signature is invalid or tampered");

    public static LicenseValidationResult Expired() => Failure("expired", "License has expired");

    public static LicenseValidationResult NotYetValid() => Failure("not_yet_valid", "License is not yet valid");

    public static LicenseValidationResult InvalidFormat() => Failure("invalid_format", "License format is invalid");
}
