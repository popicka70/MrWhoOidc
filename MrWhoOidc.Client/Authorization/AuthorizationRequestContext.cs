namespace MrWhoOidc.Client.Authorization;

public sealed class AuthorizationRequestContext
{
    public required Uri RequestUri { get; init; }
    public required string State { get; init; }
    public string? Nonce { get; init; }
    public string? CodeVerifier { get; init; }
}
