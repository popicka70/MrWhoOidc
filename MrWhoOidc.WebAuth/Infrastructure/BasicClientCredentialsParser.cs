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

            // RFC 6749 §2.3.1 requires client_id and client_secret to be
            // application/x-www-form-urlencoded before they are placed in the
            // Basic header, so percent escapes must be decoded here. Deliberately
            // do not treat '+' as a space: many non-compliant clients send base64
            // secrets verbatim, and a compliant client encodes a literal '+' as
            // %2B, which Uri.UnescapeDataString restores correctly.
            var clientId = DecodeBasicAuthComponent(decoded[..separatorIndex]);
            var clientSecret = DecodeBasicAuthComponent(decoded[(separatorIndex + 1)..]);
            return (clientId, clientSecret);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string DecodeBasicAuthComponent(string value)
    {
        // Uri.UnescapeDataString decodes %XX (including UTF-8 sequences) and
        // leaves '+' untouched, which keeps both RFC 6749-compliant clients and
        // clients that send base64 secrets verbatim working.
        return value.Contains('%') ? Uri.UnescapeDataString(value) : value;
    }
}
