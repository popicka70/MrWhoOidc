using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for validating passwords against tenant-specific password policies.
/// </summary>
public interface IPasswordPolicyService
{
    /// <summary>
    /// Default minimum password length used when no tenant policy is defined.
    /// Single source of truth for the min-8 password policy; the class-level
    /// <c>PasswordPolicyService.DefaultMinLength</c> references this constant.
    /// </summary>
    public const int DefaultMinLength = 8;

    /// <summary>
    /// Validates a password against the current tenant's password policy.
    /// </summary>
    /// <param name="password">Password to validate</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with success status and error messages</returns>
    Task<PasswordValidationResult> ValidatePasswordAsync(string password, CancellationToken ct = default);
}

/// <summary>
/// Result of password validation.
/// </summary>
public sealed record PasswordValidationResult
{
    /// <summary>
    /// Whether the password passed validation.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Error messages if validation failed.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static PasswordValidationResult Success() => new() { IsValid = true };

    /// <summary>
    /// Creates a failed validation result with error messages.
    /// </summary>
    public static PasswordValidationResult Failure(params string[] errors)
        => new() { IsValid = false, Errors = errors };
}

internal sealed class PasswordPolicyService(ITenantSettingsService settingsService) : IPasswordPolicyService
{
    /// <summary>
    /// Default minimum password length used when no tenant policy is defined.
    /// Kept in sync with <see cref="IPasswordPolicyService.DefaultMinLength"/>.
    /// </summary>
    public const int DefaultMinLength = IPasswordPolicyService.DefaultMinLength;

    public async Task<PasswordValidationResult> ValidatePasswordAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordValidationResult.Failure("Password cannot be empty.");
        }

        var settings = await settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);
        var policy = settings.Auth?.PasswordPolicy;

        // If no policy is defined, use sensible defaults (min 8 chars)
        var minLength = policy?.MinLength ?? DefaultMinLength;
        var requireUppercase = policy?.RequireUppercase ?? false;
        var requireLowercase = policy?.RequireLowercase ?? false;
        var requireDigit = policy?.RequireDigit ?? false;
        var requireSpecialChar = policy?.RequireSpecialChar ?? false;

        var errors = new List<string>();

        // Check minimum length
        if (password.Length < minLength)
        {
            errors.Add($"Password must be at least {minLength} characters long.");
        }

        // Check uppercase requirement
        if (requireUppercase && !password.Any(char.IsUpper))
        {
            errors.Add("Password must contain at least one uppercase letter.");
        }

        // Check lowercase requirement
        if (requireLowercase && !password.Any(char.IsLower))
        {
            errors.Add("Password must contain at least one lowercase letter.");
        }

        // Check digit requirement
        if (requireDigit && !password.Any(char.IsDigit))
        {
            errors.Add("Password must contain at least one digit.");
        }

        // Check special character requirement
        if (requireSpecialChar && !password.Any(c => !char.IsLetterOrDigit(c)))
        {
            errors.Add("Password must contain at least one special character.");
        }

        return errors.Count == 0
            ? PasswordValidationResult.Success()
            : PasswordValidationResult.Failure(errors.ToArray());
    }
}
