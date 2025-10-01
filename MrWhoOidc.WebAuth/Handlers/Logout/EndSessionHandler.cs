using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Orchestrates OIDC end_session flow with front-channel and back-channel notifications.
/// </summary>
public sealed class EndSessionHandler(
    FrontChannelLogoutNotifier frontChannelNotifier,
    BackChannelLogoutEnqueuer backChannelEnqueuer,
    PostLogoutRedirectValidator redirectValidator,
    IAuditSink audit,
    OidcMetrics metrics,
    ILogger<EndSessionHandler> logger)
{
    /// <summary>
    /// Handles the OIDC end_session endpoint, performing local sign-out and coordinating
    /// front-channel iframes and back-channel logout notifications.
    /// </summary>
    public async Task<IResult> ExecuteAsync(HttpContext http, LogoutRequest request, string issuer)
    {
        // Sign out local session
        await http.SignOutAsync().ConfigureAwait(false);

        // Build front-channel iframe URLs
        var iframes = await frontChannelNotifier.GetFrontChannelIframeUrlsAsync(
            issuer, 
            request.IdTokenHint, 
            request.Sid, 
            http.RequestAborted).ConfigureAwait(false);

        // Enqueue back-channel logout notifications
        await backChannelEnqueuer.EnqueueNotificationsAsync(
            http, 
            issuer, 
            request.IdTokenHint, 
            request.Sid, 
            http.RequestAborted).ConfigureAwait(false);

        // Validate post_logout_redirect_uri and create opaque reference if provided
        string? refId = null;

        if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri) && string.IsNullOrEmpty(request.ClientId))
        {
            var host = TryGetHost(request.PostLogoutRedirectUri);
            audit.Emit("logout.redirect.rejected_missing_client", new 
            { 
                post_logout_host = host, 
                post_logout_hash = audit.HashValue(request.PostLogoutRedirectUri) 
            });
            metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "post_logout_missing_client"));
            logger.LogWarning("Rejecting post_logout_redirect_uri without client_id. host={Host}", host ?? "unknown");
        }
        else if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri) && !string.IsNullOrEmpty(request.ClientId))
        {
            refId = await redirectValidator.ValidateAndCreateReferenceAsync(
                request.PostLogoutRedirectUri, 
                request.ClientId!, 
                request.State, 
                http.RequestAborted).ConfigureAwait(false);
        }

        // Render HTML page with front-channel iframes and optional redirect
        var html = FrontChannelPageBuilder.BuildPage(iframes, refId, request.State);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static string? TryGetHost(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : null;
    }
}
