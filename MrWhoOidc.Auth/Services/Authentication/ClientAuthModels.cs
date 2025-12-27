using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Authentication;

/// <summary>
/// Defines the context in which client authentication is being performed.
/// </summary>
public enum ClientAuthenticationUsage
{
    TokenEndpoint,
    Introspection,
    Revocation,
    Other
}

/// <summary>
/// Represents the input for client authentication, decoupled from HTTP.
/// </summary>
public record ClientCredentialInput(
    string ClientId,
    ClientAuthenticationUsage Usage = ClientAuthenticationUsage.Other,
    string? GrantType = null,
    string? ClientSecret = null,
    string? ClientAssertionType = null,
    string? ClientAssertion = null,
    string? MtlsThumbprint = null,
    string? EndpointUrl = null);

/// <summary>
/// Represents the result of a client authentication attempt.
/// </summary>
public record ClientAuthResult(
    bool IsSuccess,
    Client? Client = null,
    string? Error = null,
    string? ErrorDescription = null);

