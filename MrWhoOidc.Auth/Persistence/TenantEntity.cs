using System.ComponentModel.DataAnnotations;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Represents a tenant in the multi-tenant OIDC system.
/// In single-tenant mode, only the default tenant exists.
/// In multi-tenant mode, multiple tenants can exist with isolation.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Identification
    [MaxLength(100)]
    public string Slug { get; set; } = string.Empty; // URL-safe identifier, unique

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty; // Display name

    [MaxLength(500)]
    public string? Description { get; set; }

    // Issuer configuration (computed based on mode)
    [MaxLength(500)]
    public string IssuerUri { get; set; } = string.Empty; // Computed as {base}/t/{slug} in multi-tenant mode

    // Status and lifecycle
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SuspendedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; } // Soft delete

    // Branding
    [MaxLength(200)]
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Reference to uploaded tenant icon/logo. 
    /// Takes precedence over LogoUrl when both are present.
    /// </summary>
    public Guid? TenantIconId { get; set; }

    /// <summary>
    /// Navigation property to the uploaded tenant icon
    /// </summary>
    public TenantIcon? TenantIcon { get; set; }

    [MaxLength(50)]
    public string? PrimaryColor { get; set; }

    [MaxLength(50)]
    public string? AccentColor { get; set; }

    /// <summary>
    /// Brand title shown on the login page and used as the browser tab title suffix. Falls back to
    /// the tenant name when not set.
    /// </summary>
    [MaxLength(200)]
    public string? LoginTitle { get; set; }

    /// <summary>Favicon URL served by the tenant's own site (link rel="icon").</summary>
    [MaxLength(500)]
    public string? FaviconUrl { get; set; }

    /// <summary>Public terms-of-service URL linked from the tenant's footer.</summary>
    [MaxLength(500)]
    public string? TermsOfServiceUrl { get; set; }

    /// <summary>Public privacy-policy URL linked from the tenant's footer.</summary>
    [MaxLength(500)]
    public string? PrivacyPolicyUrl { get; set; }

    /// <summary>
    /// Custom CSS injected (with the CSP nonce) into every page of this tenant. Rendering escapes
    /// HTML breakouts; treat it as tenant-scoped branding power, not as a scripting channel.
    /// </summary>
    [MaxLength(10000)]
    public string? CustomCss { get; set; }

    // Configuration overrides (JSON)
    [MaxLength(4000)]
    public string? SettingsJson { get; set; } // Per-tenant OIDC/Auth/QR settings

    // Limits and quotas
    public int MaxUsers { get; set; } = 10000;

    public int MaxClients { get; set; } = 100;

    public int MaxIdentityProviders { get; set; } = 10;

    // Contact and billing
    [MaxLength(256)]
    public string? AdminEmail { get; set; }

    [MaxLength(100)]
    public string? BillingPlan { get; set; } // Free, Starter, Pro, Enterprise

    public DateTimeOffset? TrialEndsAt { get; set; }

    // Licensing
    /// <summary>
    /// Determines how this tenant's effective license is resolved.
    /// InheritPlatform: Tenant automatically inherits all platform license features.
    /// Sublicense: Tenant requires its own license (must be subset of platform features).
    /// </summary>
    public TenantLicenseMode LicenseMode { get; set; } = TenantLicenseMode.InheritPlatform;

    // Metadata
    [MaxLength(2000)]
    public string? MetadataJson { get; set; } // Extensibility: custom fields, integrations
}

/// <summary>
/// Status of a tenant.
/// </summary>
public enum TenantStatus
{
    Active = 1,
    Suspended = 2,      // Temporary disable (billing issue, abuse)
    PendingSetup = 3,   // Newly created, not yet ready
    Deleted = 4         // Soft deleted
}
