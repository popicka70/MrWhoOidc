using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// "Do you want to sign out?" page for logout requests that do not prove where they came from. Signing out on a
/// bare GET or a cross-site POST let any page log the user out (logout CSRF); the confirmation is a same-site,
/// antiforgery-protected POST back to the same endpoint.
/// </summary>
public static class LogoutConfirmationPage
{
    /// <summary>Form field that marks the user's own confirmation POST.</summary>
    public const string ConfirmField = "confirm_logout";

    /// <summary>
    /// True for a POST carrying <see cref="ConfirmField"/> and a valid antiforgery token, i.e. the user submitted
    /// the confirmation page. A missing antiforgery service fails closed.
    /// </summary>
    public static async Task<bool> IsConfirmedAsync(HttpContext http)
    {
        if (!HttpMethods.IsPost(http.Request.Method) || !http.Request.HasFormContentType)
        {
            return false;
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
        if (!string.Equals(form[ConfirmField].ToString(), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var antiforgery = http.RequestServices.GetService<IAntiforgery>();
        return antiforgery is not null && await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false);
    }

    /// <summary>Renders the confirmation form, posting <paramref name="fields"/> back to the current path.</summary>
    public static IResult Render(HttpContext http, IEnumerable<KeyValuePair<string, string?>> fields, string cancelUrl)
    {
        var antiforgery = http.RequestServices.GetService<IAntiforgery>();
        var tokens = antiforgery?.GetAndStoreTokens(http);
        http.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        http.Response.Headers.Pragma = "no-cache";

        var action = (http.Request.PathBase + http.Request.Path).Value ?? "/";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
        sb.Append("<title>Sign out</title></head><body>");
        sb.Append("<main style=\"font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:32rem;margin:3rem auto;padding:0 1rem;\">");
        sb.Append("<h1>Sign out?</h1>");
        sb.Append("<p>Do you want to sign out of your account?</p>");
        sb.Append("<form method=\"post\" action=\"").Append(WebUtility.HtmlEncode(action)).Append("\">");
        if (tokens?.RequestToken is { } requestToken)
        {
            AppendHidden(sb, tokens.FormFieldName, requestToken);
        }
        AppendHidden(sb, ConfirmField, "1");
        foreach (var (name, value) in fields)
        {
            if (!string.IsNullOrEmpty(value))
            {
                AppendHidden(sb, name, value);
            }
        }
        sb.Append("<button type=\"submit\" data-testid=\"logout-confirm\">Sign out</button> ");
        sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(cancelUrl)).Append("\">Stay signed in</a>");
        sb.Append("</form></main></body></html>");
        return Results.Content(sb.ToString(), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static void AppendHidden(StringBuilder sb, string name, string value)
        => sb.Append("<input type=\"hidden\" name=\"").Append(WebUtility.HtmlEncode(name))
            .Append("\" value=\"").Append(WebUtility.HtmlEncode(value)).Append("\"/>");
}
