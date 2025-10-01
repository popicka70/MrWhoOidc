namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Immutable record representing a logout request context.
/// </summary>
public sealed record LogoutRequest(
    string? ReturnUrl,
    string? Style,
    string? ClientId,
    string? PostLogoutRedirectUri,
    string? State,
    string? IdTokenHint,
    string? Sid)
{
    /// <summary>
    /// Parses a logout request from HTTP query parameters.
    /// </summary>
    public static LogoutRequest FromQuery(IQueryCollection query)
    {
        return new LogoutRequest(
            ReturnUrl: query["returnUrl"].ToString(),
            Style: query["style"].ToString(),
            ClientId: query["client_id"].ToString(),
            PostLogoutRedirectUri: query["post_logout_redirect_uri"].ToString(),
            State: query["state"].ToString(),
            IdTokenHint: query["id_token_hint"].ToString(),
            Sid: query["sid"].ToString()
        );
    }
}
