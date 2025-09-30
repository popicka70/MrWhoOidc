namespace MrWhoOidc.Client.Tokens;

public sealed class TokenResult
{
    private TokenResult() { }

    public bool IsError { get; private init; }
    public string? Error { get; private init; }
    public string? ErrorDescription { get; private init; }
    public string? AccessToken { get; private init; }
    public string? RefreshToken { get; private init; }
    public string? TokenType { get; private init; }
    public long? ExpiresIn { get; private init; }
    public string? IdToken { get; private init; }
    public string? Scope { get; private init; }
    public string? Raw { get; private init; }

    internal static TokenResult FromSuccess(TokenResponsePayload payload) => new()
    {
        AccessToken = payload.AccessToken,
        RefreshToken = payload.RefreshToken,
        TokenType = payload.TokenType,
        ExpiresIn = payload.ExpiresIn,
        IdToken = payload.IdToken,
        Scope = payload.Scope,
        Raw = payload.Raw
    };

    internal static TokenResult FromError(string error, string? description, string? raw) => new()
    {
        IsError = true,
        Error = error,
        ErrorDescription = description,
        Raw = raw
    };
}

internal sealed record TokenResponsePayload(string? AccessToken, string? RefreshToken, string? TokenType, long? ExpiresIn, string? IdToken, string? Scope, string? Raw);
