using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;
using System.Web;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Orchestrates OIDC end_session flow with front-channel and back-channel notifications.
/// </summary>
public sealed class EndSessionHandler(
    FrontChannelLogoutNotifier frontChannelNotifier,
    BackChannelLogoutEnqueuer backChannelEnqueuer,
    LogoutTargetResolver targetResolver,
    PostLogoutRedirectValidator redirectValidator,
    ITokenValidator tokenValidator,
    IAuditSink audit,
    OidcEndpointMetrics metrics,
    ILogger<EndSessionHandler> logger,
    ITenantSupportAccessService? supportAccessService = null)
{
    private const string EndSessionPath = "/connect/endsession";

    /// <summary>
    /// Entry point for /connect/endsession (GET or POST). Ends the session straight away only when the request
    /// carries a verified id_token_hint for the signed-in user; otherwise the user confirms first, so a third-party
    /// page cannot log them out (logout CSRF).
    /// </summary>
    public async Task<IResult> HandleAsync(HttpContext http, LogoutRequest request, string issuer)
    {
        if (await LogoutConfirmationPage.IsConfirmedAsync(http).ConfigureAwait(false)
            || await HasMatchingHintAsync(http, request, issuer).ConfigureAwait(false))
        {
            return await ExecuteAsync(http, request, issuer).ConfigureAwait(false);
        }

        audit.Emit("logout.endsession.confirmation_required", new { has_hint = !string.IsNullOrEmpty(request.IdTokenHint) });
        var path = (http.Request.PathBase + http.Request.Path).Value ?? string.Empty;
        var cancelUrl = path.EndsWith(EndSessionPath, StringComparison.OrdinalIgnoreCase) ? path[..^EndSessionPath.Length] + "/" : "/";
        return LogoutConfirmationPage.Render(http,
        [
            new("id_token_hint", request.IdTokenHint),
            new("client_id", request.ClientId),
            new("post_logout_redirect_uri", request.PostLogoutRedirectUri),
            new("state", request.State),
        ], cancelUrl);
    }

    /// <summary>
    /// A verified id_token_hint for the session's own user proves the request comes from an RP the user signed in
    /// to. Without a visible session it is accepted only on GET: browsers send the (SameSite=Lax) session cookie on
    /// a top-level GET but not on a cross-site POST, so an anonymous POST may still be aimed at a live session.
    /// </summary>
    private async Task<bool> HasMatchingHintAsync(HttpContext http, LogoutRequest request, string issuer)
    {
        if (string.IsNullOrWhiteSpace(request.IdTokenHint))
        {
            return false;
        }

        var hasSession = http.User.Identity?.IsAuthenticated == true;
        if (!hasSession && !HttpMethods.IsGet(http.Request.Method))
        {
            return false;
        }

        // ResolveSubjectAsync reports the hint's client only when the hint verified and names the session's user.
        var subject = await targetResolver.ResolveSubjectAsync(http.User, request.IdTokenHint, issuer, http.RequestAborted).ConfigureAwait(false);
        return subject?.HintClientId is not null;
    }

    /// <summary>
    /// Performs the end_session logout: local sign-out and coordinating front-channel iframes and back-channel
    /// logout notifications. Callers must have established that the request is legitimate (see <see cref="HandleAsync"/>).
    /// </summary>
    public async Task<IResult> ExecuteAsync(HttpContext http, LogoutRequest request, string issuer)
    {
        // End any active support access session before sign-out
        if (supportAccessService is not null)
        {
            await supportAccessService.StopSupportAccessAsync(http).ConfigureAwait(false);
        }

        // Capture the OP principal before sign-out: together with a *verified* id_token_hint it is the
        // only authoritative source of who is logging out.
        var currentUser = http.User;

        // Explicitly clear both browser-facing schemes used by WebAuth.
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        await http.SignOutAsync("preauth").ConfigureAwait(false);

        var subject = await targetResolver.ResolveSubjectAsync(currentUser, request.IdTokenHint, issuer, http.RequestAborted).ConfigureAwait(false);
        var targets = subject is null
            ? Array.Empty<LogoutTarget>()
            : await targetResolver.GetTargetsAsync(subject, http.RequestAborted).ConfigureAwait(false);

        // Build front-channel iframe URLs
        var iframes = frontChannelNotifier.BuildIframeUrls(issuer, targets);

        // Enqueue back-channel logout notifications
        await backChannelEnqueuer.EnqueueNotificationsAsync(http, issuer, targets, http.RequestAborted).ConfigureAwait(false);

        // Validate post_logout_redirect_uri and create opaque reference if provided
        string? refId = null;

        var invalidPostLogoutRedirect = false;

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
                invalidPostLogoutRedirect = true;
            }
            else
            {
                refId = await redirectValidator.ValidateAndCreateReferenceAsync(
                    request.PostLogoutRedirectUri,
                    effectiveClientId,
                    request.State,
                    http.RequestAborted).ConfigureAwait(false);

                invalidPostLogoutRedirect = refId is null;
            }
        }

        if (invalidPostLogoutRedirect)
        {
            var errorCspNonce = http.Items.TryGetValue("csp-nonce", out var errorNonceValue)
                ? errorNonceValue as string
                : null;
            var errorHtml = BuildInvalidPostLogoutRedirectPage(errorCspNonce);
            return Results.Content(errorHtml, "text/html; charset=utf-8", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }

        // When there are no front-channel iframes to render, redirect straight to the
        // final logout destination. This completes the flow with an HTTP redirect instead
        // of relying on client-side JavaScript, which non-JS user agents cannot follow.
        if (refId is not null && iframes.Count == 0)
        {
            return Results.Redirect("/logout/final?ref=" + Uri.EscapeDataString(refId));
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

        // id_token_hint used only to infer client_id for redirect-URI validation; audience not relevant at this step.
        var validation = await tokenValidator.ValidateAsync(idTokenHint, issuer, skipAudienceValidation: true).ConfigureAwait(false);
        if (!validation.ok)
        {
            logger.LogInformation("id_token_hint validation failed while inferring client_id for logout redirect.");
            return null;
        }

        // Prefer azp when present (multiple audiences); else aud.
        return JwtLightParser.TryGetClaim(idTokenHint, "azp")
            ?? JwtLightParser.TryGetAudience(idTokenHint);
    }

    private static string BuildInvalidPostLogoutRedirectPage(string? cspNonce)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><title>Logout</title><meta http-equiv=\"cache-control\" content=\"no-cache\"/></head><body>");
        sb.Append("<main style=\"font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:42rem;margin:3rem auto;padding:0 1rem;\">");
        sb.Append("<h1>Logout request invalid</h1>");
        sb.Append("<p>The supplied post_logout_redirect_uri is not registered for this client.</p>");
        sb.Append("<p>You have been signed out of the current session.</p>");
        sb.Append("</main>");
        if (!string.IsNullOrWhiteSpace(cspNonce))
        {
            sb.Append("<script nonce=\"");
            sb.Append(HttpUtility.HtmlAttributeEncode(cspNonce));
            sb.Append("\"></script>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
