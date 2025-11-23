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

    public static LicenseValidationResult ScopeMismatch(string? message = null) => Failure(
        "scope_mismatch",
        string.IsNullOrWhiteSpace(message)
            ? "License scope does not match the selected install target"
            : message!);

    public static LicenseValidationResult TenantMismatch(string? message = null) => Failure(
        "tenant_mismatch",
        string.IsNullOrWhiteSpace(message)
            ? "License does not apply to the selected tenant"
            : message!);

    public static LicenseValidationResult PlatformOnlyFeatureNotAllowed(string featureName) => Failure(
        "platform_feature_not_allowed",
        $"Feature '{featureName}' can only be enabled on the platform license.");
}
