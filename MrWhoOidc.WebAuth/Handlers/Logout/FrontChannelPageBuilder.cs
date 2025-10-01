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
    public static string BuildPage(IEnumerable<string> iframeUrls, string? refId, string? state)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.Append("<!DOCTYPE html><html><head>");
        sb.Append("<title>Logout</title>");
        sb.Append("<meta http-equiv=\"cache-control\" content=\"no-cache\"/>");
        sb.Append("</head><body>");

        // Add hidden iframes for each RP front-channel logout URI
        foreach (var src in iframeUrls)
        {
            sb.Append("<iframe src=\"");
            sb.Append(HttpUtility.HtmlAttributeEncode(src));
            sb.Append("\" style=\"display:none;width:0;height:0;border:0\"></iframe>");
        }

        // Auto-redirect to final page if we have a reference ID
        if (!string.IsNullOrEmpty(refId))
        {
            var finalUrl = "/logout/final?ref=" + HttpUtility.UrlEncode(refId);
            sb.Append("<script>setTimeout(function(){ window.location.replace('");
            sb.Append(HttpUtility.JavaScriptStringEncode(finalUrl));
            sb.Append("'); }, 200);</script>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}
