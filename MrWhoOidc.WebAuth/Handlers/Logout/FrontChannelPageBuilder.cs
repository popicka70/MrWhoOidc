using System.Web;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Builds HTML pages with front-channel logout iframes.
/// </summary>
public static class FrontChannelPageBuilder
{
    /// <summary>
    /// Creates an HTML page with hidden iframes for front-channel logout notifications
    /// and optional auto-redirect to final logout page.
    /// </summary>
    public static string BuildPage(IEnumerable<string> iframeUrls, string? refId, string? state, string? cspNonce)
    {
        var sb = new System.Text.StringBuilder();

        var finalUrl = string.IsNullOrEmpty(refId)
            ? null
            : "/logout/final?ref=" + HttpUtility.UrlEncode(refId);

        sb.Append("<!DOCTYPE html><html><head>");
        sb.Append("<title>Logout</title>");
        sb.Append("<meta http-equiv=\"cache-control\" content=\"no-cache\"/>");

        // Non-JS fallback: ensure the final logout redirect happens even when the user agent
        // does not execute the script below. A short delay lets front-channel iframes load first.
        if (finalUrl is not null)
        {
            sb.Append("<meta http-equiv=\"refresh\" content=\"1;url=");
            sb.Append(HttpUtility.HtmlAttributeEncode(finalUrl));
            sb.Append("\"/>");
        }

        sb.Append("</head><body>");

        // Add hidden iframes for each RP front-channel logout URI
        foreach (var src in iframeUrls)
        {
            sb.Append("<iframe src=\"");
            sb.Append(HttpUtility.HtmlAttributeEncode(src));
            sb.Append("\" style=\"display:none;width:0;height:0;border:0\"></iframe>");
        }

        // Terminal logout case (no post-logout redirect): show a visible confirmation so the user
        // (and certification reviewers capturing a screenshot) can see the logout completed.
        if (finalUrl is null)
        {
            sb.Append("<main style=\"font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;max-width:42rem;margin:3rem auto;padding:0 1rem;\">");
            sb.Append("<h1>You have been signed out</h1>");
            sb.Append("<p>Your session has ended. You may now close this window.</p>");
            sb.Append("</main>");
        }

        // Auto-redirect to final page if we have a reference ID
        if (finalUrl is not null)
        {
            sb.Append("<script");
            if (!string.IsNullOrWhiteSpace(cspNonce))
            {
                sb.Append(" nonce=\"");
                sb.Append(HttpUtility.HtmlAttributeEncode(cspNonce));
                sb.Append("\"");
            }

            sb.Append(">setTimeout(function(){ window.location.replace('");
            sb.Append(HttpUtility.JavaScriptStringEncode(finalUrl));
            sb.Append("'); }, 200);</script>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}
