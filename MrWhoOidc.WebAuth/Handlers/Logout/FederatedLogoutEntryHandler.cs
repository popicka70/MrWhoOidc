using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Infrastructure;
using MrWhoOidc.WebAuth.Infrastructure.Security;
using MrWhoOidc.WebAuth.Observability;
using System.Web;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Determines if federated logout is available and redirects to prompt page.
/// </summary>
public sealed class FederatedLogoutEntryHandler(
    IUpstreamLogoutService upstreamLogoutSvc,
    IOptions<FederatedLogoutOptions> fedOpts,
    ILogger<FederatedLogoutEntryHandler> logger,
    IAuditSink audit,
    OidcEndpointMetrics metrics,
    LocalLogoutHandler localLogout)
{
    /// <summary>
    /// Checks if the user can federate logout and redirects to prompt or performs local logout.
    /// A local logout of a signed-in user happens only once <paramref name="confirmed"/> (the user submitted the
    /// antiforgery-protected confirmation); otherwise the confirmation page is shown, so a link or image pointing
    /// at /logout cannot sign the user out. The federated prompt page is itself such a confirmation.
    /// </summary>
    public async Task<IResult> ExecuteAsync(HttpContext http, LogoutRequest request, bool confirmed = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!fedOpts.Value.Enabled)
        {
            logger.LogInformation("Federated logout disabled - performing local logout");
            audit.Emit("logout.federated.prompt.skip_disabled", new { });
            return await LocalLogoutAsync(http, request, confirmed).ConfigureAwait(false);
        }

        var capability = await upstreamLogoutSvc.CanFederateAsync(http.User, http.RequestAborted).ConfigureAwait(false);
        if (!capability.CanFederate)
        {
            audit.Emit("logout.federated.prompt.skip_no_capability", new { });
            return await LocalLogoutAsync(http, request, confirmed).ConfigureAwait(false);
        }

        var idpDisplay = capability.ProviderDisplayName ?? capability.ProviderName;
        var formReturn = string.IsNullOrEmpty(request.ReturnUrl) ? "/" : request.ReturnUrl;

        audit.Emit("logout.federated.prompt", new { provider = capability.ProviderName });
        metrics.LogoutRequests.Add(1, new KeyValuePair<string, object?>("mode", "prompt"));
        metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "prompt"));

        var qStyle = string.IsNullOrEmpty(request.Style) ? string.Empty : $"&style={HttpUtility.UrlEncode(request.Style)}";
        var qClient = string.IsNullOrEmpty(request.ClientId) ? string.Empty : $"&client_id={HttpUtility.UrlEncode(request.ClientId)}";
        var qPlru = string.IsNullOrEmpty(request.PostLogoutRedirectUri) ? string.Empty : $"&post_logout_redirect_uri={HttpUtility.UrlEncode(request.PostLogoutRedirectUri)}";

        return Results.Redirect($"/logout/prompt?provider={HttpUtility.UrlEncode(idpDisplay)}&ret={HttpUtility.UrlEncode(formReturn)}{qStyle}{qClient}{qPlru}");
    }

    private async Task<IResult> LocalLogoutAsync(HttpContext http, LogoutRequest request, bool confirmed)
    {
        if (!confirmed && http.User.Identity?.IsAuthenticated == true)
        {
            return LogoutConfirmationPage.Render(http,
            [
                new("returnUrl", request.ReturnUrl),
                new("style", request.Style),
            ], SafeRedirect.LocalOrDefault(request.ReturnUrl));
        }

        return await localLogout.ExecuteAsync(http, request.ReturnUrl).ConfigureAwait(false);
    }
}
