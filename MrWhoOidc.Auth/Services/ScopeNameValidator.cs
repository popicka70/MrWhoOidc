using System.Text.RegularExpressions;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Validates scope names according to naming conventions:
/// - Global scopes: Standard OAuth2/OIDC names (openid, profile, email, etc.)
/// - Tenant scopes: Must use {tenant-slug}.{suffix} format (e.g., acme.reports.read)
/// </summary>
public interface IScopeNameValidator
{
    /// <summary>
    /// Validates a scope name according to context (global or tenant-scoped).
    /// </summary>
    /// <param name="scopeName">The scope name to validate.</param>
    /// <param name="isGlobal">True if this is a global scope.</param>
    /// <param name="tenantSlug">The tenant slug (required for tenant-scoped scopes).</param>
    /// <returns>Validation result with success status and error message if invalid.</returns>
    ScopeNameValidationResult ValidateScopeName(string scopeName, bool isGlobal, string? tenantSlug = null);
}

/// <summary>
/// Result of scope name validation.
/// </summary>
public sealed class ScopeNameValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    
    public static ScopeNameValidationResult Success() => new() { IsValid = true };
    public static ScopeNameValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
}

internal sealed class ScopeNameValidator : IScopeNameValidator
{
    // Standard OAuth2/OIDC scopes that are reserved for global use
    private static readonly HashSet<string> StandardScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openid",
        "profile",
        "email",
        "address",
        "phone",
        "offline_access",
        "roles"
    };
    
    // Pattern for valid scope names (lowercase alphanumeric with dots, hyphens, underscores)
    private static readonly Regex ValidScopePattern = new(@"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$", RegexOptions.Compiled);
    
    // Pattern for tenant scope suffix (after the tenant prefix)
    private static readonly Regex TenantSuffixPattern = new(@"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$", RegexOptions.Compiled);

    public ScopeNameValidationResult ValidateScopeName(string scopeName, bool isGlobal, string? tenantSlug = null)
    {
        // Basic validation
        if (string.IsNullOrWhiteSpace(scopeName))
        {
            return ScopeNameValidationResult.Error("Scope name cannot be empty.");
        }

        scopeName = scopeName.Trim();

        // Length validation
        if (scopeName.Length < 2)
        {
            return ScopeNameValidationResult.Error("Scope name must be at least 2 characters long.");
        }

        if (scopeName.Length > 100)
        {
            return ScopeNameValidationResult.Error("Scope name cannot exceed 100 characters.");
        }

        if (isGlobal)
        {
            return ValidateGlobalScope(scopeName);
        }
        else
        {
            return ValidateTenantScope(scopeName, tenantSlug);
        }
    }

    private ScopeNameValidationResult ValidateGlobalScope(string scopeName)
    {
        // Global scopes should be standard OAuth2/OIDC scopes
        // Platform admins are trusted, so we allow them to create custom global scopes
        // but we warn if they use dot notation (which is reserved for tenant scopes)
        
        if (scopeName.Contains('.'))
        {
            return ScopeNameValidationResult.Error(
                "Global scopes should not use dot notation. Use simple names like 'openid', 'profile', etc.");
        }

        if (!ValidScopePattern.IsMatch(scopeName))
        {
            return ScopeNameValidationResult.Error(
                "Invalid scope name format. Use lowercase letters, numbers, hyphens, and underscores only.");
        }

        return ScopeNameValidationResult.Success();
    }

    private ScopeNameValidationResult ValidateTenantScope(string scopeName, string? tenantSlug)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            return ScopeNameValidationResult.Error("Tenant slug is required for tenant-scoped scopes.");
        }

        // Check if trying to use a standard scope name
        if (StandardScopes.Contains(scopeName))
        {
            return ScopeNameValidationResult.Error(
                $"Cannot use reserved standard scope name '{scopeName}'. Standard scopes are global only.");
        }

        // Tenant scopes must use prefix format: {tenant-slug}.{suffix}
        var expectedPrefix = $"{tenantSlug}.";
        if (!scopeName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ScopeNameValidationResult.Error(
                $"Tenant scope must start with '{expectedPrefix}'. Example: '{tenantSlug}.reports.read'");
        }

        // Extract and validate the suffix
        var suffix = scopeName.Substring(expectedPrefix.Length);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return ScopeNameValidationResult.Error(
                $"Scope name cannot be just the tenant prefix. Add a suffix like '{tenantSlug}.read'");
        }

        if (!TenantSuffixPattern.IsMatch(suffix))
        {
            return ScopeNameValidationResult.Error(
                "Invalid scope suffix format. Use lowercase letters, numbers, dots, hyphens, and underscores only.");
        }

        // Prevent common misconfigurations
        if (suffix.StartsWith('.') || suffix.EndsWith('.'))
        {
            return ScopeNameValidationResult.Error("Scope suffix cannot start or end with a dot.");
        }

        if (suffix.Contains(".."))
        {
            return ScopeNameValidationResult.Error("Scope suffix cannot contain consecutive dots.");
        }

        return ScopeNameValidationResult.Success();
    }
}
