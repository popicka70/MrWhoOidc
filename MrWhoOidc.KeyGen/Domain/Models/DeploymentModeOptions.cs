namespace MrWhoOidc.KeyGen.Domain.Models;

/// <summary>
/// Deployment mode for platform licenses indicating whether the platform
/// operates in single-tenant or multi-tenant mode.
/// </summary>
public static class DeploymentModeOptions
{
    /// <summary>
    /// Single-tenant deployment where platform license applies to one tenant only.
    /// In this mode, there's no need for sublicenses.
    /// </summary>
    public const string SingleTenant = "single-tenant";

    /// <summary>
    /// Multi-tenant deployment where platform license applies to all tenants.
    /// Tenants can optionally use sublicenses for more restrictive licensing.
    /// </summary>
    public const string MultiTenant = "multi-tenant";

    /// <summary>
    /// Validates whether the given deployment mode is valid.
    /// </summary>
    public static bool IsValid(string? mode)
        => string.Equals(mode, SingleTenant, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, MultiTenant, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the claim value for the given deployment mode.
    /// </summary>
    public static string ToClaimValue(string mode)
        => string.Equals(mode, SingleTenant, StringComparison.OrdinalIgnoreCase)
            ? SingleTenant
            : MultiTenant;
}
