using System;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Delegation;

/// <summary>
/// Central capability catalog for delegated access grants.
/// Defines which capabilities are delegable and their metadata constraints.
/// Implements AD-3: Delegate capabilities, not roles.
/// </summary>
public sealed record DelegableCapabilityDefinition(
    string Name,
    string DisplayName,
    string Description,
    bool IsDelegable,
    bool RequiresStepUp,
    TimeSpan MaximumGrantLifetime,
    IReadOnlySet<string> AllowedResourceTypes);

/// <summary>
/// Catalog of all known capabilities with their delegability metadata.
/// Provides a way to check if a capability is delegable and get its definition.
/// Implements AD-3 and AD-4: Deny by default for unknown capabilities.
/// </summary>
public sealed class DelegableCapabilityCatalog
    : IDelegableCapabilityCatalog
{
    private readonly Dictionary<string, DelegableCapabilityDefinition> _capabilitiesByName;

    /// <summary>
    /// Creates the catalog initialized with the allowlist from Section 6.3.
    /// Validates that at least one delegable capability exists (startup safety check).
    /// </summary>
    public DelegableCapabilityCatalog()
    {
        var capabilities = new List<DelegableCapabilityDefinition>(16);

        // profile.read — Delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "profile.read",
            "Read profile",
            "Read delegator-visible profile data allowed by resource policy",
            true,
            false,
            TimeSpan.FromDays(7),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "user" }));

        // profile.update_limited — Candidate (not delegable in initial release)
        capabilities.Add(new DelegableCapabilityDefinition(
            "profile.update_limited",
            "Update profile (limited)",
            "Update selected profile fields excluding email, credentials, MFA, recovery, and legal identity",
            false,
            true,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "profile" }));

        // documents.read — Candidate (not delegable in initial release)
        capabilities.Add(new DelegableCapabilityDefinition(
            "documents.read",
            "Read documents",
            "Read documents owned or controlled by the delegator",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "document" }));

        // documents.manage — Candidate (not delegable in initial release)
        capabilities.Add(new DelegableCapabilityDefinition(
            "documents.manage",
            "Manage documents",
            "Create, update, or delete documents owned or controlled by the delegator",
            false,
            true,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "document" }));

        // approvals.review — Candidate (not delegable in initial release)
        capabilities.Add(new DelegableCapabilityDefinition(
            "approvals.review",
            "Review approvals",
            "Review approval requests that require delegator consent",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "approval" }));

        // sessions.read — Non-delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "sessions.read",
            "Read sessions",
            "Read active session information for the delegator",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "session" }));

        // sessions.revoke — Non-delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "sessions.revoke",
            "Revoke sessions",
            "Revoke sessions owned or controlled by the delegator",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "session" }));

        // credentials.* — Non-delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "credentials.*",
            "Manage credentials",
            "Password, MFA, WebAuthn, recovery, linked identities",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "credential" }));

        // delegation.manage — Non-delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "delegation.manage",
            "Manage delegations",
            "Create, modify, or revoke delegated access grants",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "delegation" }));

        // consent.manage — Non-delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "consent.manage",
            "Manage consent",
            "Create or withdraw user consent for clients and scopes",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "consent" }));

        // tenant_admin.* — Non-delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "tenant_admin.*",
            "Tenant administration",
            "Tenant administrator capabilities (not delegable — delegation is not role assignment)",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tenant_admin" }));

        // client_secret.* — Non-delegable
        capabilities.Add(new DelegableCapabilityDefinition(
            "client_secret.*",
            "Manage client secrets",
            "Administrative credential operation for client secrets",
            false,
            false,
            TimeSpan.MaxValue,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "client_secret" }));

        // Build dictionary keyed by Name for O(1) lookup.
        var dict = new Dictionary<string, DelegableCapabilityDefinition>(capabilities.Count);
        foreach (var def in capabilities)
        {
            dict.Add(def.Name, def);
        }

        // Startup validation: ensure the catalog is not empty and contains at least one delegable capability.
        if (dict.Count == 0)
        {
            throw new StartupValidationError("DelegableCapabilityCatalog must contain at least one capability definition.");
        }

        if (!dict.Values.Any(x => x.IsDelegable))
        {
            throw new StartupValidationError(
                "DelegableCapabilityCatalog must contain at least one delegable capability " +
                $"(e.g., 'profile.read'). Catalog has {dict.Count} entries, none delegable.");
        }

        _capabilitiesByName = dict;
    }

    /// <summary>
    /// Returns the definition for a capability by name, or null if the capability is unknown.
    /// Implements AD-4: Unknown capabilities are non-delegable.
    /// </summary>
    public DelegableCapabilityDefinition? GetDefinition(string name)
        => _capabilitiesByName.GetValueOrDefault(name);

    /// <summary>
    /// Returns true if the named capability is known and marked as delegable.
    /// Returns false for unknown capabilities (AD-4: deny by default).
    /// </summary>
    public bool IsDelegable(string name)
    {
        var def = _capabilitiesByName.GetValueOrDefault(name);
        return def is not null && def.IsDelegable;
    }

    /// <summary>
    /// Returns an immutable snapshot of the full catalog for iteration.
    /// </summary>
    public IReadOnlyDictionary<string, DelegableCapabilityDefinition> AllDefinitions()
        => _capabilitiesByName.AsReadOnly();
}

/// <summary>
/// Error thrown when a startup validation check fails.
/// Prevents the application from starting in an inconsistent state.
/// </summary>
public sealed class StartupValidationError : Exception
{
    public StartupValidationError(string message) : base(message) { }
}

/// <summary>
/// Interface for the delegated access capability catalog.
/// Provides a stable contract for checking capability delegability and retrieving definitions.
/// </summary>
public interface IDelegableCapabilityCatalog
{
    /// <summary>
    /// Returns the definition for a capability by name, or null if unknown.
    /// </summary>
    DelegableCapabilityDefinition? GetDefinition(string name);

    /// <summary>
    /// Returns true if the named capability is known and marked as delegable.
    /// </summary>
    bool IsDelegable(string name);

    /// <summary>
    /// Returns an immutable snapshot of all registered definitions.
    /// </summary>
    IReadOnlyDictionary<string, DelegableCapabilityDefinition> AllDefinitions();
}
