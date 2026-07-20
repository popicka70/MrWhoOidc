using System;
using MrWhoOidc.Auth.Services.Delegation;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Maps between internal capabilities and OAuth scopes.
/// Implements Section 6.9: Capability-to-scope mapping for delegated token exchange.
/// Capabilities follow the pattern <resource>.<action> (e.g., "profile.read").
/// OAuth scopes follow the standard short-scope naming (e.g., "profile").
/// Unknown or unmappable capabilities are excluded from the result.
/// </summary>
public interface IScopeMapper
{
    /// <summary>
    /// Translate internal capabilities into OAuth scopes.
    /// Each capability is mapped using the known mapping table.
    /// Capabilities that have no mapping are silently excluded.
    /// Duplicate scopes are deduplicated in the result.
    /// </summary>
    IEnumerable<string> MapCapabilitiesToScopes(IEnumerable<string> capabilities);

    /// <summary>
    /// Translate an OAuth scope back to the capabilities that could produce it.
    /// Returns all capabilities whose mapped scope matches the given scope.
    /// Returns an empty collection if the scope is unknown or unmappable.
    /// </summary>
    IEnumerable<string> MapScopeToCapabilities(string scope);
}
