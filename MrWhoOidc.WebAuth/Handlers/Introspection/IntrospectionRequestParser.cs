using System.Text;
using MrWhoOidc.Auth.Protocols;

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
            return (null, ErrorResults.InvalidRequest("Content-Type must be application/x-www-form-urlencoded"));
        }

        var (clientIdHeader, clientSecretHeader) = ReadClientCredentials(http);
        var form = await http.Request.ReadFormAsync().ConfigureAwait(false);

        var token = form[OAuthConstants.Parameters.Token].ToString();
        var hint = form[OAuthConstants.Parameters.TokenTypeHint].ToString();
        var clientId = !string.IsNullOrEmpty(clientIdHeader) ? clientIdHeader : form[OAuthConstants.Parameters.ClientId].ToString();
        var clientSecret = !string.IsNullOrEmpty(clientSecretHeader) ? clientSecretHeader : form[OAuthConstants.Parameters.ClientSecret].ToString();
        var clientAssertionType = form[OAuthConstants.Parameters.ClientAssertionType].ToString();
        var clientAssertion = form[OAuthConstants.Parameters.ClientAssertion].ToString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(clientId))
        {
            return (null, ErrorResults.InvalidRequest("token and client_id are required"));
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
