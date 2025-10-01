namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Defines the contract for handling OIDC logout flows.
/// </summary>
public interface ILogoutHandler
{
    /// <summary>
    /// Performs a local logout without federated IdP interaction.
    /// </summary>
    Task<IResult> LocalLogoutAsync(HttpContext http);

    /// <summary>
    /// Initiates the logout flow, checking if federated logout is available.
    /// </summary>
    Task<IResult> LogoutEntryAsync(HttpContext http);

    /// <summary>
    /// Handles the callback from an upstream IdP after federated logout.
    /// </summary>
    Task<IResult> FederatedCallbackAsync(HttpContext http);

    /// <summary>
    /// Handles OIDC RP-initiated end_session requests with front-channel and back-channel notifications.
    /// </summary>
    Task<IResult> EndSessionAsync(HttpContext http);

    /// <summary>
    /// Resolves and redirects to a validated logout redirect URI using an opaque reference.
    /// </summary>
    Task<IResult> FinalRedirectAsync(HttpContext http);
}
