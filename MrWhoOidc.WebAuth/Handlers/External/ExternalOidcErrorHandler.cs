using System.Text;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Handles error responses for external OIDC flows.
/// </summary>
public interface IExternalOidcErrorHandler
{
    IResult CreateFriendlyError(string? returnUrl, string? clientId, string? correlationHandle, string message, string? code = null);
    IResult CreateConfirmPage(string token, string? returnUrl, string? clientId, string? correlationId, string email, string targetUserDisplay);
}

internal sealed class ExternalOidcErrorHandler : IExternalOidcErrorHandler
{
    public IResult CreateFriendlyError(string? returnUrl, string? clientId, string? correlationHandle, string message, string? code = null)
    {
        // SECURITY: never reflect the internal/diagnostic `message` to the browser. The detailed
        // message is logged server-side by the callers; the user-facing page derives a generic,
        // safe message from the stable `code` plus the correlation ID for support follow-up.
        var qp = new Dictionary<string, string?>
        {
            ["cid_ref"] = correlationHandle,
            ["code"] = code,
            ["returnUrl"] = returnUrl,
            ["clientId"] = clientId
        };

        var qb = System.Web.HttpUtility.ParseQueryString(string.Empty);
        foreach (var kv in qp)
        {
            if (!string.IsNullOrEmpty(kv.Value))
                qb[kv.Key] = kv.Value;
        }

        var url = "/auth/external/error?" + qb.ToString();
        return Results.Redirect(url);
    }

    public IResult CreateConfirmPage(string token, string? returnUrl, string? clientId, string? correlationId, string email, string targetUserDisplay)
    {
        var builder = new StringBuilder();
        builder.Append("<html><head><title>Confirm account linking</title>");
        builder.Append("<link rel=\"stylesheet\" href=\"/lib/bootstrap/dist/css/bootstrap.min.css\" />");
        builder.Append("</head><body class=\"container py-4\">");
        builder.Append("<div class=\"alert alert-info\"><strong>Confirm account linking</strong></div>");
        builder.Append("<p>We found an existing account for <code>");
        builder.Append(System.Web.HttpUtility.HtmlEncode(email));
        builder.Append("</code> (\"");
        builder.Append(System.Web.HttpUtility.HtmlEncode(targetUserDisplay));
        builder.Append("\"). Do you want to link this external identity to your existing account?</p>");
        builder.Append("<div class=\"mt-3\">");
        builder.Append($"<a class=\"btn btn-primary me-2\" href=\"/auth/external/confirm?t={Uri.EscapeDataString(token)}\">Yes, link and continue</a>");
        builder.Append($"<a class=\"btn btn-secondary\" href=\"/auth/external/confirm?t={Uri.EscapeDataString(token)}&cancel=1\">Cancel</a>");
        builder.Append("</div>");
        builder.Append("</body></html>");

        return Results.Content(builder.ToString(), "text/html; charset=utf-8");
    }
}
