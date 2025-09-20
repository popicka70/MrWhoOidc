namespace MrWhoOidc.Auth.Protocols;

public class AuthorizeRequest
{
    public string? response_type { get; set; }
    public string? client_id { get; set; }
    public string? redirect_uri { get; set; }
    public string? scope { get; set; }
    public string? state { get; set; }
    public string? nonce { get; set; }
    public string? code_challenge { get; set; }
    public string? code_challenge_method { get; set; }
}

public class AuthorizeValidationResult
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }

    public string? ClientId { get; set; }
    public string? RedirectUri { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public string? Nonce { get; set; }
    public string? CodeChallenge { get; set; }
    public string? CodeChallengeMethod { get; set; }
    public bool RequireConsent { get; set; }
}
