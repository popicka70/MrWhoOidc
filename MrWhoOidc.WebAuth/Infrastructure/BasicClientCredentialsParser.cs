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

            // Per RFC 7617, HTTP Basic credentials are sent verbatim after base64
            // decoding — they must NOT be URL-decoded. Base64 secrets legitimately
            // contain '+' which WebUtility.UrlDecode would corrupt into a space,
            // causing intermittent secret-validation failures for client_secret_basic.
            var clientId = decoded[..separatorIndex];
            var clientSecret = decoded[(separatorIndex + 1)..];
            return (clientId, clientSecret);
        }
        catch
        {
            return (null, null);
        }
    }
}