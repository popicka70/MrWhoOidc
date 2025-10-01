using System.Text;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Parses introspection requests from HTTP context.
/// </summary>
internal static class IntrospectionRequestParser
{
    public static async Task<(IntrospectionRequest? Request, IResult? ErrorResult)> ParseAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
        {
            return (null, Results.BadRequest(new { error = "invalid_request" }));
        }

        var (clientIdHeader, clientSecretHeader) = ReadClientCredentials(http);
        var form = await http.Request.ReadFormAsync().ConfigureAwait(false);

        var token = form["token"].ToString();
        var hint = form["token_type_hint"].ToString();
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form["client_id"].ToString();
        var clientSecret = !string.IsNullOrEmpty(clientSecretHeader) ? clientSecretHeader : form["client_secret"].ToString();
        var clientAssertionType = form["client_assertion_type"].ToString();
        var clientAssertion = form["client_assertion"].ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
        {
            return (null, Results.BadRequest(new { error = "invalid_request" }));
        }

        var request = new IntrospectionRequest(
            token,
            hint,
            clientId,
            clientSecret,
            clientAssertionType,
            clientAssertion
        );

        return (request, null);
    }

    private static (string? clientId, string? clientSecret) ReadClientCredentials(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            return (null, null);
        }

        try
        {
            var raw = header.Substring("Basic ".Length).Trim();
            var bytes = Convert.FromBase64String(raw);
            var pair = Encoding.UTF8.GetString(bytes);
            var idx = pair.IndexOf(':');
            
            if (idx < 0)
            {
                return (null, null);
            }

            var id = pair[..idx];
            var secret = pair[(idx + 1)..];
            return (id, secret);
        }
        catch
        {
            return (null, null);
        }
    }
}
