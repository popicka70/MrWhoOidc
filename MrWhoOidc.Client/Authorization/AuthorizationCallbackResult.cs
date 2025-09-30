namespace MrWhoOidc.Client.Authorization;

public sealed class AuthorizationCallbackResult
{
    public bool IsError => !string.IsNullOrEmpty(Error);

    public string? Error { get; init; }

    public string? ErrorDescription { get; init; }

    public string? ErrorUri { get; init; }

    public string? Code { get; init; }

    public string? State { get; init; }

    public string? Nonce { get; init; }

    public string? CodeVerifier { get; init; }

    public bool IsJarmResponse { get; init; }

    public string? ResponseJwt { get; init; }
}
