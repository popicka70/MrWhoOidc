namespace MrWhoOidc.Client.Tokens;

public sealed class TokenExchangeRequest
{
    public required string SubjectToken { get; init; }
    public string SubjectTokenType { get; init; } = "urn:ietf:params:oauth:token-type:access_token";
    public string GrantType { get; init; } = "urn:ietf:params:oauth:grant-type:token-exchange";
    public string? RequestedTokenType { get; init; }
    public string? Resource { get; init; }
    public string? Audience { get; init; }
    public string? Scope { get; init; }
    public IDictionary<string, string?> AdditionalParameters { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}
