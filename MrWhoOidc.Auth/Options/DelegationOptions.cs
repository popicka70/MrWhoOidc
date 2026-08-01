using System;

namespace MrWhoOidc.Auth.Options;

/// <summary>
/// Configuration options for delegated access grant policies.
/// Controls grant lifetimes, acceptance windows, and step-up requirements.
/// Implements AD-5: Use durable records with immediate revocation.
/// </summary>
public sealed class DelegationOptions
{
    /// <summary>
    /// Default lifetime for newly created grants before they expire.
    /// Default: 1440 minutes (24 hours).
    /// </summary>
    public int DefaultGrantLifetimeMinutes { get; set; } = 1440;

    /// <summary>
    /// Maximum allowed grant lifetime. No grant may exceed this value.
    /// Default: 43200 minutes (30 days).
    /// </summary>
    public int MaximumGrantLifetimeMinutes { get; set; } = 43200;

    /// <summary>
    /// Window within which the delegate must accept or decline the invitation.
    /// Default: 1440 minutes (24 hours).
    /// </summary>
    public int AcceptanceWindowMinutes { get; set; } = 1440;

    /// <summary>
    /// If true, sensitive capabilities require a step-up (MFA) before the
    /// delegate can exercise them. Step-up is checked at authorization time.
    /// </summary>
    public bool RequireStepUpForSensitiveCapabilities { get; set; } = true;
}
