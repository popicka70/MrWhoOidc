using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for accessing tenant branding configuration.
/// </summary>
public interface ITenantBrandingService
{
    /// <summary>
    /// Gets the branding configuration for a specific tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <returns>Branding configuration, or null if not found</returns>
    Task<TenantBranding?> GetBrandingAsync(Guid tenantId);

    /// <summary>
    /// Gets the branding configuration for the current tenant from context.
    /// </summary>
    /// <returns>Branding configuration, or default branding if not found</returns>
    Task<TenantBranding> GetCurrentTenantBrandingAsync();
}

/// <summary>
/// Represents tenant branding configuration.
/// </summary>
public class TenantBranding
{
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? AccentColor { get; set; }
    public string TenantName { get; set; } = string.Empty;

    /// <summary>Brand title for the login page and the browser tab title suffix.</summary>
    public string? LoginTitle { get; set; }

    /// <summary>Favicon URL served by the tenant's own site.</summary>
    public string? FaviconUrl { get; set; }

    /// <summary>Public terms-of-service URL.</summary>
    public string? TermsOfServiceUrl { get; set; }

    /// <summary>Public privacy-policy URL.</summary>
    public string? PrivacyPolicyUrl { get; set; }

    /// <summary>Raw custom CSS as stored on the tenant.</summary>
    public string? CustomCss { get; set; }

    /// <summary>
    /// The suffix used for the browser tab title: the login title when set, otherwise the
    /// platform's default product name.
    /// </summary>
    public string TitleSuffix => string.IsNullOrWhiteSpace(LoginTitle) ? "MrWhoOidc.WebAuth" : LoginTitle;

    /// <summary>
    /// Custom CSS safe to embed in a <c>&lt;style&gt;</c> tag: anything that could close the
    /// element early (a literal <c>&lt;/style</c> or an HTML comment opener) is stripped, so the
    /// tenant branding input cannot escape its style scope. CSS itself has no use for these
    /// sequences outside comments and string content.
    /// </summary>
    public string? SafeCustomCss
    {
        get
        {
            if (string.IsNullOrEmpty(CustomCss))
                return null;

            var css = CustomCss.Replace("</style", string.Empty, StringComparison.OrdinalIgnoreCase)
                               .Replace("<!--", string.Empty, StringComparison.OrdinalIgnoreCase)
                               .Replace("-->", string.Empty, StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrWhiteSpace(css) ? null : css;
        }
    }

    /// <summary>
    /// Gets the primary color or a default if not set.
    /// </summary>
    public string GetPrimaryColorOrDefault() => PrimaryColor ?? "#007bff";

    /// <summary>
    /// Gets the accent color or a default if not set.
    /// </summary>
    public string GetAccentColorOrDefault() => AccentColor ?? "#6c757d";

    /// <summary>
    /// Gets whether custom branding is configured.
    /// </summary>
    public bool HasCustomBranding =>
        !string.IsNullOrEmpty(LogoUrl) ||
        !string.IsNullOrEmpty(PrimaryColor) ||
        !string.IsNullOrEmpty(AccentColor) ||
        !string.IsNullOrEmpty(LoginTitle) ||
        !string.IsNullOrEmpty(FaviconUrl) ||
        !string.IsNullOrEmpty(TermsOfServiceUrl) ||
        !string.IsNullOrEmpty(PrivacyPolicyUrl) ||
        !string.IsNullOrEmpty(CustomCss);
}
