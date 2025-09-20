using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IAuthorizeHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class AuthorizeHandler(IAuthorizeService authorize, IAuthorizationCodeService codes, IConsentService consents, OidcMetrics metrics, IAuthorizationCodeMetadataStore meta, IPushedAuthorizationRequestStore parStore) : IAuthorizeHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var sw = Stopwatch.StartNew();
        string outcome = "redirect";
        try
        {
            metrics.AuthorizeRequests.Add(1);

            // If request_uri is provided, try to resolve the pushed request and merge it
            string? requestUri = http.Request.Query["request_uri"];
            bool isPar = !string.IsNullOrEmpty(requestUri);
            AuthorizeRequest effectiveReq;
            if (isPar)
            {
                var entry = parStore.TryGet(requestUri!);
                if (entry is null)
                {
                    outcome = "error";
                    return ErrorResults.InvalidRequest("Invalid or expired request_uri");
                }
                // Build from stored request, but allow state to be provided at request time (optional per spec)
                effectiveReq = entry.Request;
                var stateFromQuery = http.Request.Query["state"].ToString();
                if (!string.IsNullOrEmpty(stateFromQuery)) effectiveReq.state = stateFromQuery;
            }
            else
            {
                effectiveReq = new AuthorizeRequest
                {
                    response_type = http.Request.Query["response_type"],
                    client_id = http.Request.Query["client_id"],
                    redirect_uri = http.Request.Query["redirect_uri"],
                    scope = http.Request.Query["scope"],
                    state = http.Request.Query["state"],
                    nonce = http.Request.Query["nonce"],
                    code_challenge = http.Request.Query["code_challenge"],
                    code_challenge_method = http.Request.Query["code_challenge_method"],
                    resource = http.Request.Query["resource"],
                };
            }

            var validation = await authorize.ValidateAsync(effectiveReq);
            if (!validation.IsValid)
            {
                outcome = "error";
                if (!string.IsNullOrEmpty(effectiveReq.redirect_uri))
                {
                    var uri = new UriBuilder(effectiveReq.redirect_uri);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    query["error"] = validation.Error;
                    query["error_description"] = validation.ErrorDescription;
                    if (!string.IsNullOrEmpty(effectiveReq.state)) query["state"] = effectiveReq.state;
                    uri.Query = query.ToString();
                    return Results.Redirect(uri.ToString());
                }
                return ErrorResults.InvalidRequest(validation.ErrorDescription);
            }

            if (!http.User.Identity?.IsAuthenticated ?? true)
            {
                outcome = "login";
                var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                return Results.Redirect($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var userId))
                return Results.Unauthorized();

            if (validation.RequireConsent && !await consents.HasConsentAsync(userId, validation.ClientId!, validation.Scopes))
            {
                outcome = "consent";
                var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
                var consentUrl = $"/consent?ClientId={Uri.EscapeDataString(validation.ClientId!)}&ReturnUrl={Uri.EscapeDataString(returnUrl)}&" + string.Join("&", validation.Scopes.Select(s => $"Scopes={Uri.EscapeDataString(s)}"));
                return Results.Redirect(consentUrl);
            }

            var (ok, _, redirect, code) = await codes.IssueAsync(validation, userId);
            if (!ok || redirect is null) return Results.Json(new { error = "server_error" }, statusCode: 500);

            // Now that authorization succeeded, consume PAR if used
            if (isPar)
            {
                parStore.MarkConsumed(requestUri!);
            }

            // Capture auth_time from login cookie claims
            var authTimeClaim = http.User.FindFirst("auth_time")?.Value;
            if (!string.IsNullOrEmpty(code) && long.TryParse(authTimeClaim, out var seconds))
            {
                meta.SetAuthTime(code!, DateTimeOffset.FromUnixTimeSeconds(seconds));
            }

            // Persist RFC 8707 resource indicator with the code (if present)
            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(validation.Resource))
            {
                meta.SetResource(code!, validation.Resource!);
            }

            if (!string.IsNullOrEmpty(effectiveReq.state))
            {
                var uri = new UriBuilder(redirect);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                query["state"] = effectiveReq.state;
                uri.Query = query.ToString();
                return Results.Redirect(uri.ToString());
            }

            return Results.Redirect(redirect);
        }
        finally
        {
            sw.Stop();
            metrics.AuthorizeDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { new("outcome", outcome) });
        }
    }
}
