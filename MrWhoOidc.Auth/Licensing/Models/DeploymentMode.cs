namespace MrWhoOidc.Auth.Licensing.Models;

/// <summary>
/// Indicates the deployment mode of the platform license.
/// </summary>
public enum DeploymentMode
{
    /// <summary>
    /// Single-tenant deployment: only one tenant exists, platform license applies directly.
    /// Multi-tenancy feature is not available.
    /// </summary>
    SingleTenant = 0,

    /// <summary>
    /// Multi-tenant deployment: multiple tenants can exist with platform license
    /// defining maximum capabilities. Tenants can inherit or have sublicenses.
    /// </summary>
    MultiTenant = 1
}

/// <summary>
/// Extension methods for <see cref="DeploymentMode"/>.
/// </summary>
public static class DeploymentModeExtensions
{
    public const string SingleTenantClaim = "single_tenant";
    public const string MultiTenantClaim = "multi_tenant";

    /// <summary>
    /// Converts a deployment mode enum to its claim string representation.
    /// </summary>
    public static string ToClaimValue(this DeploymentMode mode)
    {
        return mode switch
        {
            DeploymentMode.SingleTenant => SingleTenantClaim,
            DeploymentMode.MultiTenant => MultiTenantClaim,
            _ => MultiTenantClaim
        };
    }

    /// <summary>
    /// Parses a claim string value to a <see cref="DeploymentMode"/> enum.
    /// Defaults to <see cref="DeploymentMode.MultiTenant"/> if not recognized.
    /// </summary>
    public static DeploymentMode FromClaimValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DeploymentMode.MultiTenant;
        }

        return value.Equals(SingleTenantClaim, StringComparison.OrdinalIgnoreCase)
            ? DeploymentMode.SingleTenant
            : DeploymentMode.MultiTenant;
    }
}
