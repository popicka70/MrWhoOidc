using System;

namespace MrWhoOidc.Auth.Services.Authorization;

public record AuthorizeValidationResult(
    bool IsValid,
    string? Error = null,
    string? ErrorDescription = null,
    string? ClientId = null,
    string? RedirectUri = null,
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
);
