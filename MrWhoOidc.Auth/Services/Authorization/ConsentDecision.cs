namespace MrWhoOidc.Auth.Services.Authorization;

public record ConsentDecision(
    bool RequiresConsent,
    bool HasConsent,
    string[]? MissingScopes = null
);
