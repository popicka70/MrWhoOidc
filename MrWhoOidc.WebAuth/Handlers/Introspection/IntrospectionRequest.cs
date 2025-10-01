namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Represents a parsed introspection request.
/// </summary>
public sealed record IntrospectionRequest(
    string Token,
    string? TokenTypeHint,
    string ClientId,
    string? ClientSecret,
    string? ClientAssertionType,
    string? ClientAssertion
);
