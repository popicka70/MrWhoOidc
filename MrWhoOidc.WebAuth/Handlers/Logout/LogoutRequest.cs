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
    public static LogoutRequest FromQuery(IQueryCollection query) => From(key => query[key].ToString());

    /// <summary>
    /// Parses from the form body for a form POST (RP-Initiated Logout 1.0 §2 allows GET and POST), otherwise from
    /// the query string.
    /// </summary>
    public static async Task<LogoutRequest> FromRequestAsync(HttpRequest request)
    {
        if (HttpMethods.IsPost(request.Method) && request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
            return From(key => form[key].ToString());
        }

        return FromQuery(request.Query);
    }

    private static LogoutRequest From(Func<string, string> get) => new(
        ReturnUrl: get("returnUrl"),
        Style: get("style"),
        ClientId: get("client_id"),
        PostLogoutRedirectUri: get("post_logout_redirect_uri"),
        State: get("state"),
        IdTokenHint: get("id_token_hint"),
        Sid: get("sid"));
}
