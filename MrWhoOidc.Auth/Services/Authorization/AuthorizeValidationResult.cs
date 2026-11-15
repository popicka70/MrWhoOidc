using System;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services.Authorization;

public record AuthorizeValidationResult(
    bool IsValid,
    string? Error = null,
    string? ErrorDescription = null,
    string? ClientId = null,
    string? RedirectUri = null,
    string? ResponseType = null,
    string[]? Scopes = null,
    string? Nonce = null,
    string? CodeChallenge = null,
    string? CodeChallengeMethod = null,
    bool RequireConsent = false,
    string? Resource = null,
    string? ResponseMode = null,
    string? State = null,
    string? ClaimsJson = null,
    string[]? PromptValues = null,
    int? MaxAgeSeconds = null,
    string[]? AcrValues = null,
    string? AuthorizationDetailsJson = null
)
{
    /// <summary>
    /// True when the authorization response returns tokens in the front channel instead of an
    /// authorization code (OIDC implicit flow: response_type id_token / id_token token).
    /// </summary>
    public bool IsImplicitFlow
        => !string.IsNullOrEmpty(ResponseType)
           && !ResponseType!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Contains(OAuthConstants.ResponseTypes.Code, StringComparer.Ordinal);
}
