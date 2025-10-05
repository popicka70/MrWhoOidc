namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Service for constructing the issuer URI based on multi-tenancy mode.
/// </summary>
public interface IIssuerBuilder
{
    /// <summary>
    /// Builds the issuer URI for the current tenant context.
    /// </summary>
    /// <param name="baseUrl">The base URL (scheme + host), e.g., "https://auth.example.com"</param>
    /// <returns>The issuer URI string.</returns>
    string BuildIssuer(string baseUrl);

    /// <summary>
    /// Builds the issuer URI for a specific tenant.
    /// </summary>
    /// <param name="baseUrl">The base URL (scheme + host), e.g., "https://auth.example.com"</param>
    /// <param name="tenantSlug">The tenant slug to use for the issuer.</param>
    /// <returns>The issuer URI string.</returns>
    string BuildIssuer(string baseUrl, string tenantSlug);
}

/// <summary>
/// Mode-aware issuer builder that constructs issuer URIs based on multi-tenancy configuration.
/// </summary>
internal sealed class IssuerBuilder : IIssuerBuilder
{
    private readonly IMultiTenancyOptions _options;
    private readonly ITenantAccessor _tenantAccessor;

    public IssuerBuilder(IMultiTenancyOptions options, ITenantAccessor tenantAccessor)
    {
        _options = options;
        _tenantAccessor = tenantAccessor;
    }

    /// <summary>
    /// Builds the issuer URI for the current tenant context.
    /// In single-tenant mode: returns root issuer (e.g., https://auth.example.com)
    /// In multi-tenant mode: returns path-based issuer (e.g., https://auth.example.com/t/tenant-slug)
    /// </summary>
    public string BuildIssuer(string baseUrl)
    {
        if (!_options.Enabled)
        {
            // Single-tenant mode: root issuer
            return baseUrl;
        }

        // Multi-tenant mode: path-based issuer
        var currentTenant = _tenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            throw new InvalidOperationException("Tenant context required in multi-tenant mode");
        }

        return $"{baseUrl}/t/{currentTenant.Slug}";
    }

    /// <summary>
    /// Builds the issuer URI for a specific tenant.
    /// </summary>
    public string BuildIssuer(string baseUrl, string tenantSlug)
    {
        if (!_options.Enabled)
        {
            // Single-tenant mode: root issuer (ignore tenant slug)
            return baseUrl;
        }

        // Multi-tenant mode: path-based issuer with specified slug
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            throw new ArgumentException("Tenant slug required in multi-tenant mode", nameof(tenantSlug));
        }

        return $"{baseUrl}/t/{tenantSlug}";
    }
}
