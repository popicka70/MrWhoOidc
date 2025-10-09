namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for discovering tenants associated with a user's email address.
/// Used for email-first login flow where users don't need to know tenant URLs.
/// </summary>
public interface ITenantDiscoveryService
{
    /// <summary>
    /// Find all active tenants where the given email has a user account.
    /// Searches both primary email (User.Email) and verified alternative emails (UserAlternativeEmail).
    /// </summary>
    /// <param name="email">Email address to search for (will be normalized)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of tenant information, empty if no tenants found</returns>
    Task<List<TenantInfo>> FindTenantsByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Get user's preferred tenant based on email and optional context (IP address, cookies).
    /// Returns null if no preference found.
    /// </summary>
    /// <param name="email">Email address (will be normalized)</param>
    /// <param name="ipAddress">Optional IP address for location-aware preferences</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Preferred tenant info or null if no preference exists</returns>
    Task<TenantInfo?> GetPreferredTenantAsync(string email, string? ipAddress = null, CancellationToken ct = default);
}

/// <summary>
/// Information about a tenant for display in tenant selection UI.
/// </summary>
public class TenantInfo
{
    /// <summary>
    /// Tenant unique identifier
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Tenant slug (used in URLs: /t/{slug}/...)
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Tenant display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional tenant logo URL for branding
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Full login URL for this tenant (e.g., /t/acme/login)
    /// </summary>
    public string LoginUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional: Last time user logged into this tenant (for sorting)
    /// </summary>
    public DateTimeOffset? LastLoginAt { get; set; }
}
