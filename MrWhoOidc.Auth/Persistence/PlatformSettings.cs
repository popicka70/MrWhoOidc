using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// System-wide platform settings that apply across all tenants.
/// Single-row table pattern - only one row should exist.
/// </summary>
public class PlatformSettings
{
    /// <summary>
    /// Primary key using UUIDv7 for optimal database performance.
    /// </summary>
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// Enable QR login option on the /DiscoverTenant page.
    /// When true, users see a "Sign in with QR Code" button.
    /// Default is false (opt-in feature).
    /// </summary>
    public bool QrLoginAtDiscoveryEnabled { get; set; } = false;

    /// <summary>
    /// Enable Dynamic Client Registration endpoints (RFC 7591/7592) at runtime.
    /// Default is false (opt-in feature).
    /// </summary>
    public bool DynamicClientRegistrationEnabled { get; set; } = false;

    /// <summary>
    /// Enable OAuth 2.0 Token Exchange (RFC 8693 / OBO) at runtime.
    /// Null means "inherit configured AuthOptions default" for backward compatibility.
    /// </summary>
    public bool? EnableTokenExchange { get; set; }

    /// <summary>
    /// Timestamp when settings were first created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Timestamp when settings were last modified.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Username or identifier of the admin who last modified settings.
    /// </summary>
    [MaxLength(256)]
    public string? UpdatedBy { get; set; }
}
