using System.Collections.Immutable;

namespace MrWhoOidc.Auth.IdentityProviders;

/// <summary>
/// Defines metadata and default configuration for a well-known identity provider template.
/// </summary>
public sealed record ProviderTemplateDefinition
{
    /// <summary>
    /// The template identifier.
    /// </summary>
    public required WellKnownProviderTemplate Template { get; init; }

    /// <summary>
    /// Display name shown in the UI.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Short description of the provider.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Authority URL pattern. May contain placeholders like {tenant}, {domain}, {realm}.
    /// </summary>
    public required string AuthorityPattern { get; init; }

    /// <summary>
    /// Discovery URL pattern, or null to use standard /.well-known/openid-configuration.
    /// </summary>
    public string? DiscoveryUrlPattern { get; init; }

    /// <summary>
    /// Default scopes to request.
    /// </summary>
    public required ImmutableArray<string> DefaultScopes { get; init; }

    /// <summary>
    /// Default response type.
    /// </summary>
    public string ResponseType { get; init; } = "code";

    /// <summary>
    /// Whether PKCE should be enabled by default.
    /// </summary>
    public bool DefaultUsePkce { get; init; } = true;

    /// <summary>
    /// Whether the provider requires special client authentication (e.g., Apple's private_key_jwt).
    /// </summary>
    public bool RequiresSpecialClientAuth { get; init; }

    /// <summary>
    /// CSS class for styling the provider button.
    /// </summary>
    public string? CssClass { get; init; }

    /// <summary>
    /// SVG icon markup for the provider logo.
    /// </summary>
    public required string IconSvg { get; init; }

    /// <summary>
    /// Brand color for the provider (hex).
    /// </summary>
    public string? BrandColor { get; init; }

    /// <summary>
    /// URL to the provider's developer documentation.
    /// </summary>
    public string? DocumentationUrl { get; init; }

    /// <summary>
    /// URL to the provider's app registration console.
    /// </summary>
    public string? ConsoleUrl { get; init; }

    /// <summary>
    /// Help text for the provider configuration.
    /// </summary>
    public string? HelpText { get; init; }

    /// <summary>
    /// Placeholders that need to be filled in the authority URL.
    /// </summary>
    public ImmutableArray<AuthorityPlaceholder> AuthorityPlaceholders { get; init; } = [];

    /// <summary>
    /// Provider-specific configuration fields.
    /// </summary>
    public ImmutableArray<ProviderConfigField> ConfigFields { get; init; } = [];

    /// <summary>
    /// Default claim mappings for this provider.
    /// </summary>
    public ImmutableArray<DefaultClaimMapping> DefaultClaimMappings { get; init; } = [];

    /// <summary>
    /// Whether the provider supports back-channel logout.
    /// </summary>
    public bool SupportsBackChannelLogout { get; init; } = true;

    /// <summary>
    /// Tier level: 1 = full support, 2 = template + icon, 3 = generic.
    /// </summary>
    public int Tier { get; init; } = 1;
}

/// <summary>
/// Defines a placeholder in the authority URL pattern.
/// </summary>
public sealed record AuthorityPlaceholder
{
    /// <summary>
    /// Placeholder name (e.g., "tenant", "domain", "realm").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Display label for the field.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Help text describing what value to enter.
    /// </summary>
    public string? HelpText { get; init; }

    /// <summary>
    /// Whether this placeholder is required.
    /// </summary>
    public bool Required { get; init; } = true;

    /// <summary>
    /// Default value if any.
    /// </summary>
    public string? DefaultValue { get; init; }
    
    /// <summary>
    /// Placeholder text for the input field (example value).
    /// </summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Predefined options for selection (e.g., Entra tenant types).
    /// </summary>
    public ImmutableArray<PlaceholderOption> Options { get; init; } = [];
}

/// <summary>
/// A predefined option for a placeholder.
/// </summary>
public sealed record PlaceholderOption
{
    /// <summary>
    /// The value to use in the URL.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Display label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Description of this option.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Defines a provider-specific configuration field.
/// </summary>
public sealed record ProviderConfigField
{
    /// <summary>
    /// Field name (used as key in provider-specific config JSON).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Display label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Field type: text, password, select, checkbox, textarea.
    /// </summary>
    public string FieldType { get; init; } = "text";

    /// <summary>
    /// Help text.
    /// </summary>
    public string? HelpText { get; init; }

    /// <summary>
    /// Whether the field is required.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Default value.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Placeholder text for input fields.
    /// </summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Options for select fields.
    /// </summary>
    public ImmutableArray<PlaceholderOption> Options { get; init; } = [];
}

/// <summary>
/// Defines a default claim mapping from external to local claim.
/// </summary>
public sealed record DefaultClaimMapping
{
    /// <summary>
    /// The claim name from the external IdP.
    /// </summary>
    public required string ExternalClaim { get; init; }

    /// <summary>
    /// The local claim name to map to.
    /// </summary>
    public required string LocalClaim { get; init; }

    /// <summary>
    /// Optional transformation (e.g., "lowercase", "split:,").
    /// </summary>
    public string? Transform { get; init; }
}
