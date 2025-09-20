using System.Security.Claims;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IAuthorizeHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class AuthorizeHandler(IAuthorizeService authorize, IAuthorizationCodeService codes, IConsentService consents) : IAuthorizeHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        var req = new AuthorizeRequest
        {
            response_type = http.Request.Query["response_type"],
            client_id = http.Request.Query["client_id"],
            redirect_uri = http.Request.Query["redirect_uri"],
            scope = http.Request.Query["scope"],
            state = http.Request.Query["state"],
            nonce = http.Request.Query["nonce"],
            code_challenge = http.Request.Query["code_challenge"],
            code_challenge_method = http.Request.Query["code_challenge_method"],
        };

        var validation = await authorize.ValidateAsync(req);
        if (!validation.IsValid)
        {
            if (!string.IsNullOrEmpty(req.redirect_uri))
            {
                var uri = new UriBuilder(req.redirect_uri);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                query["error"] = validation.Error;
                query["error_description"] = validation.ErrorDescription;
                if (!string.IsNullOrEmpty(req.state)) query["state"] = req.state;
                uri.Query = query.ToString();
                return Results.Redirect(uri.ToString());
            }
            return Results.BadRequest(new { error = validation.Error, error_description = validation.ErrorDescription });
        }

        if (!http.User.Identity?.IsAuthenticated ?? true)
        {
            var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
            return Results.Redirect($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var userId))
            return Results.Unauthorized();

        if (validation.RequireConsent && !await consents.HasConsentAsync(userId, validation.ClientId!, validation.Scopes))
        {
            var returnUrl = http.Request.Path + http.Request.QueryString.ToUriComponent();
            var consentUrl = $"/consent?ClientId={Uri.EscapeDataString(validation.ClientId!)}&ReturnUrl={Uri.EscapeDataString(returnUrl)}&" + string.Join("&", validation.Scopes.Select(s => $"Scopes={Uri.EscapeDataString(s)}"));
            return Results.Redirect(consentUrl);
        }

        var (ok, _, redirect) = await codes.IssueAsync(validation, userId);
        if (!ok || redirect is null) return Results.Problem("Failed to issue code");

        if (!string.IsNullOrEmpty(req.state))
        {
            var uri = new UriBuilder(redirect);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query["state"] = req.state;
            uri.Query = query.ToString();
            return Results.Redirect(uri.ToString());
        }

        return Results.Redirect(redirect);
    }
}
