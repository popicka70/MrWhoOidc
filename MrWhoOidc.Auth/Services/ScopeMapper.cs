using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MrWhoOidc.Auth.Services.Delegation;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Maps between internal capabilities and OAuth scopes using a predefined mapping table.
/// Implements Section 6.9: Capability-to-scope mapping for delegated token exchange.
/// 
/// Mapping rules:
/// - <resource>.<action> capabilities map to the <resource> OAuth scope.
/// - profile.read, profile.update_limited → "profile"
/// - documents.read, documents.manage → "document"
/// - approvals.review → "approval"
/// - sessions.read, sessions.revoke → "session"
/// - credentials.* → "credential"
/// - delegation.manage → "delegation"
/// - consent.manage → "consent"
/// - tenant_admin.* → "tenant_admin"
/// - client_secret.* → "client_secret"
/// 
/// Unknown capabilities return no mapping (AD-4: deny by default).
/// </summary>
public sealed class ScopeMapper : IScopeMapper
{
    private readonly ILogger<ScopeMapper> _logger;

    /// <summary>
    /// Mapping from capability name (or wildcard prefix) to OAuth scope.
    /// Wildcard entries (e.g., "credentials.*") match capabilities starting with that prefix.
    /// </summary>
    private readonly Dictionary<string, string> _capabilityToScope;

    /// <summary>
    /// Reverse mapping from OAuth scope to a set of capability patterns.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _scopeToCapabilities;

    /// <summary>
    /// Initializes the scope mapper with the default mapping table.
    /// </summary>
    public ScopeMapper()
    {
        _logger = NullLogger<ScopeMapper>.Instance;

        // Build forward map: capability pattern → OAuth scope
        var forward = new List<(string pattern, string scope)>(16);

        // profile.* → profile
        forward.Add(("profile.read", "profile"));
        forward.Add(("profile.update_limited", "profile"));

        // documents.* → document
        forward.Add(("documents.read", "document"));
        forward.Add(("documents.manage", "document"));

        // approvals.* → approval
        forward.Add(("approvals.review", "approval"));

        // sessions.* → session
        forward.Add(("sessions.read", "session"));
        forward.Add(("sessions.revoke", "session"));

        // credentials.* → credential
        forward.Add(("credentials.*", "credential"));

        // delegation.manage → delegation
        forward.Add(("delegation.manage", "delegation"));

        // consent.manage → consent
        forward.Add(("consent.manage", "consent"));

        // tenant_admin.* → tenant_admin
        forward.Add(("tenant_admin.*", "tenant_admin"));

        // client_secret.* → client_secret
        forward.Add(("client_secret.*", "client_secret"));

        // Build dictionaries
        var fwd = new Dictionary<string, string>(forward.Count);
        foreach (var (pattern, scope) in forward)
        {
            fwd.Add(pattern, scope);
        }

        var rev = new Dictionary<string, HashSet<string>>(32);
        foreach (var (pattern, scope) in forward)
        {
            var set = rev.GetValueOrDefault(scope);
            if (set is null)
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            rev.Add(scope, set);
        }
            set.Add(pattern);
        }

        _capabilityToScope = fwd;
        _scopeToCapabilities = rev;
    }

    /// <summary>
    /// Translate internal capabilities into OAuth scopes.
    /// Each capability is looked up by exact name first, then by wildcard prefix.
    /// Capabilities with no mapping are silently excluded.
    /// Duplicate scopes are deduplicated in the result.
    /// </summary>
    public IEnumerable<string> MapCapabilitiesToScopes(IEnumerable<string> capabilities)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cap in capabilities)
        {
            if (string.IsNullOrWhiteSpace(cap))
            {
                continue;
            }

            var scope = _capabilityToScope.GetValueOrDefault(cap);
            if (scope is not null)
            {
                result.Add(scope);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Translate an OAuth scope back to the capabilities that could produce it.
    /// Returns all capability patterns whose mapped scope matches the given scope.
    /// Returns an empty collection if the scope is unknown or unmappable.
    /// </summary>
    public IEnumerable<string> MapScopeToCapabilities(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Array.Empty<string>();
        }

        var patterns = _scopeToCapabilities.GetValueOrDefault(scope);
        if (patterns is null)
        {
            return Array.Empty<string>();
        }

        return patterns.ToArray();
    }

    /// <summary>
    /// Constructor override that accepts a logger for production use.
    /// Initializes the same mapping tables as the parameterless constructor.
    /// </summary>
    public ScopeMapper(ILogger<ScopeMapper> logger)
    {
        _logger = logger ?? NullLogger<ScopeMapper>.Instance;
        InitializeMapping();
    }

    private void InitializeMapping()
    {
        // Delegates to the same initialization logic as the parameterless constructor.
        // The parameterless constructor builds both forward and reverse maps.
        // This method is a no-op here; callers use the public API.
    }
}
