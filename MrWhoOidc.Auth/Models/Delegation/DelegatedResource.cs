using System;

namespace MrWhoOidc.Auth.Models.Delegation;

/// <summary>
/// Represents a resource being accessed in a delegated authorization context.
/// The resource type identifies which capability category the resource belongs to,
/// and the resource identifier provides the specific resource for constraint matching.
/// </summary>
public sealed record DelegatedResource(
    string Type,
    string? Id,
    string? ConstraintsJson);

/// <summary>
/// Typed resource constraints for delegated access grants.
/// Maps each capability to its allowed resource types and optional resource identifiers.
/// </summary>
public sealed record ResourceConstraint(
    string Capability,
    IReadOnlySet<string> AllowedTypes,
    IReadOnlySet<string>? AllowedIds);
