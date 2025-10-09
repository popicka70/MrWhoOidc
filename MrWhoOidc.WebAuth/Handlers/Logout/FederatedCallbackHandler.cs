using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Observability;
using System.Net;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Handles the callback from an upstream IdP after federated logout.
/// </summary>
public sealed class FederatedCallbackHandler(
    IUpstreamLogoutService upstreamLogoutSvc,
    IAuditSink audit,
    OidcMetrics metrics)
{
    /// <summary>
    /// Validates the federated callback state and redirects appropriately.
    /// </summary>
    public async Task<IResult> ExecuteAsync(HttpContext http)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = http.Request.Query["state"].ToString();

        var validation = await upstreamLogoutSvc.ValidateCallbackAsync(state, http.RequestAborted).ConfigureAwait(false);

        if (!validation.Valid)
        {
            metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", validation.Reason ?? "invalid_state"));
            metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "federated_callback_fail"));
            audit.Emit("logout.federated.callback.page.fail", new { reason = validation.Reason });

            var reasonParam = validation.Reason ?? "invalid_state";
            return Results.Redirect($"/Logout/FederatedCallbackError?reason={WebUtility.UrlEncode(reasonParam)}");
        }

        metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "federated_callback"));
        audit.Emit("logout.federated.callback.page.ok", new { has_ref = validation.RefId != null });

        if (!string.IsNullOrWhiteSpace(validation.RefId))
        {
            return Results.Redirect($"/logout/final?ref={Uri.EscapeDataString(validation.RefId!)}");
        }

        if (!string.IsNullOrWhiteSpace(validation.ReturnUrl))
        {
            return Results.Redirect(validation.ReturnUrl);
        }

        return Results.Redirect("/Logout/FederatedSignedOut");
    }
}
