using Microsoft.AspNetCore.Authentication;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Orchestrates OIDC end_session flow with front-channel and back-channel notifications.
/// </summary>
public sealed class EndSessionHandler(
    FrontChannelLogoutNotifier frontChannelNotifier,
    BackChannelLogoutEnqueuer backChannelEnqueuer,
    PostLogoutRedirectValidator redirectValidator,
    ITokenValidator tokenValidator,
    IAuditSink audit,
    OidcEndpointMetrics metrics,
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

        if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri))
        {
            var effectiveClientId = !string.IsNullOrEmpty(request.ClientId)
                ? request.ClientId
                : await TryInferClientIdFromIdTokenHintAsync(request.IdTokenHint, issuer, tokenValidator, logger).ConfigureAwait(false);

            if (string.IsNullOrEmpty(effectiveClientId))
            {
                var host = TryGetHost(request.PostLogoutRedirectUri);
                audit.Emit("logout.redirect.rejected_missing_client", new
                {
                    post_logout_host = host,
                    post_logout_hash = audit.HashValue(request.PostLogoutRedirectUri)
                });
                metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "post_logout_missing_client"));
                logger.LogWarning("Rejecting post_logout_redirect_uri without a resolvable client_id. host={Host}", host ?? "unknown");
            }
            else
            {
                refId = await redirectValidator.ValidateAndCreateReferenceAsync(
                    request.PostLogoutRedirectUri,
                    effectiveClientId,
                    request.State,
                    http.RequestAborted).ConfigureAwait(false);
            }
        }

        // Render HTML page with front-channel iframes and optional redirect
        var cspNonce = http.Items.TryGetValue("csp-nonce", out var nonceValue)
            ? nonceValue as string
            : null;
        var html = FrontChannelPageBuilder.BuildPage(iframes, refId, request.State, cspNonce);
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

    private static async Task<string?> TryInferClientIdFromIdTokenHintAsync(
        string? idTokenHint,
        string issuer,
        ITokenValidator tokenValidator,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(idTokenHint) || !JwtLightParser.IsProbablyJwt(idTokenHint))
        {
            return null;
        }

        var validation = await tokenValidator.ValidateAsync(idTokenHint, issuer).ConfigureAwait(false);
        if (!validation.ok)
        {
            logger.LogInformation("id_token_hint validation failed while inferring client_id for logout redirect.");
            return null;
        }

        // Prefer azp when present (multiple audiences); else aud.
        return JwtLightParser.TryGetClaim(idTokenHint, "azp")
            ?? JwtLightParser.TryGetAudience(idTokenHint);
    }
}
