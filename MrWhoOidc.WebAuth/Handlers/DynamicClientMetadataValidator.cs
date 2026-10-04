using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// Client metadata validation shared by RFC 7591 registration (POST /register) and
/// RFC 7592 client configuration updates (PUT /register/{client_id}), so both paths
/// enforce exactly the same rules.
/// </summary>
internal static class DynamicClientMetadataValidator
{
    /// <summary>
    /// Validates a redirect_uri or post_logout_redirect_uri using an allowlist:
    /// <list type="bullet">
    /// <item><c>https</c> URIs;</item>
    /// <item><c>http</c> only for loopback hosts (RFC 8252 §7.3);</item>
    /// <item>private-use schemes only in reverse-domain form, i.e. containing a '.' (RFC 8252 §7.1).</item>
    /// </list>
    /// Fragments (RFC 6749 §3.1.2) and userinfo components are always rejected.
    /// </summary>
    /// <returns>An error description, or <c>null</c> when the URI is acceptable.</returns>
    public static string? ValidateRedirectUri(string? uri, string parameterName = "redirect_uri")
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return $"Invalid {parameterName}: {uri}";
        }

        if (uri.Contains('#'))
        {
            return $"{parameterName} must not contain a fragment";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return $"{parameterName} must not contain userinfo";
        }

        var scheme = parsed.Scheme;
        if (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return parsed.IsLoopback
                ? null
                : $"http {parameterName}s are only allowed for loopback hosts";
        }

        // Private-use URI schemes for native apps must be reverse-domain names (RFC 8252 §7.1).
        // This also rejects javascript:, data:, file:, vbscript: and any other non-app scheme.
        if (scheme.Contains('.'))
        {
            return null;
        }

        return $"{parameterName} scheme '{scheme}' is not allowed";
    }

    /// <summary>
    /// RFC 8705 §2.2: a <c>self_signed_tls_client_auth</c> client registers its certificate(s) as
    /// <c>x5c</c> entries in an inline <c>jwks</c>. Resolves their <c>x5t#S256</c> thumbprints (the
    /// format matched at the token endpoint). For any other auth method the result is <c>null</c>.
    /// </summary>
    /// <returns>An error description, or <c>null</c> on success.</returns>
    public static string? ResolveMtlsThumbprints(string authMethod, object? jwks, out string? thumbprintsJson)
    {
        thumbprintsJson = null;
        if (!string.Equals(authMethod, "self_signed_tls_client_auth", StringComparison.Ordinal))
        {
            return null;
        }

        const string error = "self_signed_tls_client_auth requires an inline jwks containing the client certificate (x5c)";
        if (jwks is null)
        {
            return error;
        }

        var thumbprints = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(jwks));
            if (!doc.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            {
                return error;
            }

            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object
                    || !key.TryGetProperty("x5c", out var x5c)
                    || x5c.ValueKind != JsonValueKind.Array
                    || x5c.GetArrayLength() == 0)
                {
                    continue;
                }

                // x5c entries are standard base64 DER certificates (RFC 7517 §4.7); the first is the client's.
                var der = Convert.FromBase64String(x5c[0].GetString() ?? string.Empty);
                using var certificate = X509CertificateLoader.LoadCertificate(der);
                thumbprints.Add(Base64UrlEncoder.Encode(SHA256.HashData(certificate.RawData)));
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or CryptographicException)
        {
            return "jwks contains an invalid x5c certificate";
        }

        if (thumbprints.Count == 0)
        {
            return error;
        }

        thumbprintsJson = JsonSerializer.Serialize(thumbprints.Distinct(StringComparer.Ordinal).ToList());
        return null;
    }
}
