using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MrWhoOidc.WebAuth.Infrastructure;

internal static class BasicClientCredentialsParser
{
    public static (string? clientId, string? clientSecret) ReadFromAuthorizationHeader(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !AuthenticationHeaderValue.TryParse(authorizationHeader, out var parsed))
        {
            return (null, null);
        }

        if (!string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(parsed.Parameter))
        {
            return (null, null);
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
            var separatorIndex = decoded.IndexOf(':');
            if (separatorIndex < 0)
            {
                return (null, null);
            }

            var clientId = WebUtility.UrlDecode(decoded[..separatorIndex]);
            var clientSecret = WebUtility.UrlDecode(decoded[(separatorIndex + 1)..]);
            return (clientId, clientSecret);
        }
        catch
        {
            return (null, null);
        }
    }
}