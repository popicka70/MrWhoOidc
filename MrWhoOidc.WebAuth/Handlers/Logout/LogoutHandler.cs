namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Main orchestrator for all logout flows, delegating to specialized handlers.
/// </summary>
public sealed class LogoutHandler(
    LocalLogoutHandler localLogout,
    FederatedLogoutEntryHandler federatedEntry,
    FederatedCallbackHandler federatedCallback,
    EndSessionHandler endSession,
    LogoutRedirectResolver redirectResolver) : ILogoutHandler
{
    /// <summary>
    /// Performs a local logout without federated IdP interaction.
    /// </summary>
    public Task<IResult> LocalLogoutAsync(HttpContext http)
    {
        var request = LogoutRequest.FromQuery(http.Request.Query);
        return localLogout.ExecuteAsync(http, request.ReturnUrl);
    }

    /// <summary>
    /// Initiates the logout flow, checking if federated logout is available.
    /// </summary>
    public Task<IResult> LogoutEntryAsync(HttpContext http)
    {
        var request = LogoutRequest.FromQuery(http.Request.Query);
        return federatedEntry.ExecuteAsync(http, request);
    }

    /// <summary>
    /// Handles the callback from an upstream IdP after federated logout.
    /// </summary>
    public Task<IResult> FederatedCallbackAsync(HttpContext http)
    {
        return federatedCallback.ExecuteAsync(http);
    }

    /// <summary>
    /// Handles OIDC RP-initiated end_session requests with front-channel and back-channel notifications.
    /// </summary>
    public Task<IResult> EndSessionAsync(HttpContext http)
    {
        var request = LogoutRequest.FromQuery(http.Request.Query);
        var issuer = http.GetIssuer();
        return endSession.ExecuteAsync(http, request, issuer);
    }

    /// <summary>
    /// Resolves and redirects to a validated logout redirect URI using an opaque reference.
    /// </summary>
    public Task<IResult> FinalRedirectAsync(HttpContext http)
    {
        var refId = http.Request.Query["ref"].ToString();
        return redirectResolver.ResolveAndRedirectAsync(refId, http.RequestAborted);
    }
}
