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
    /// Validates an <c>{prefix}_encrypted_response_alg</c>/<c>_enc</c> pair (<c>id_token</c> or <c>userinfo</c>).
    /// Per OpenID Connect Dynamic Client Registration §2, <c>_enc</c> requires <c>_alg</c>, and an omitted
    /// <c>_enc</c> defaults to <c>A128CBC-HS256</c>. Only RSA-OAEP + A256CBC-HS512 is implemented, so an
    /// omitted <c>_enc</c> is rejected rather than silently stored as "no encryption".
    /// </summary>
    /// <returns>An error description, or <c>null</c> when acceptable.</returns>
    public static string? ValidateEncryption(string prefix, string? alg, string? enc)
    {
        if (string.IsNullOrEmpty(alg))
        {
            return string.IsNullOrEmpty(enc)
                ? null
                : $"{prefix}_encrypted_response_enc requires {prefix}_encrypted_response_alg";
        }

        if (!string.Equals(alg, "RSA-OAEP", StringComparison.Ordinal))
        {
            return $"{prefix}_encrypted_response_alg must be 'RSA-OAEP'";
        }

        if (string.IsNullOrEmpty(enc))
        {
            return $"{prefix}_encrypted_response_enc defaults to 'A128CBC-HS256', which is not supported; specify 'A256CBC-HS512'";
        }

        return string.Equals(enc, "A256CBC-HS512", StringComparison.Ordinal)
            ? null
            : $"{prefix}_encrypted_response_enc must be 'A256CBC-HS512'";
    }

    /// <summary>
    /// A <c>private_key_jwt</c> client must register the keys its assertions are verified with;
    /// without <c>jwks</c> or <c>jwks_uri</c> it could never authenticate.
    /// </summary>
    /// <returns>An error description, or <c>null</c> when acceptable.</returns>
    public static string? ValidatePrivateKeyJwtKeys(string authMethod, object? jwks, string? jwksUri)
    {
        return string.Equals(authMethod, "private_key_jwt", StringComparison.Ordinal)
            && jwks is null
            && string.IsNullOrWhiteSpace(jwksUri)
                ? "private_key_jwt requires jwks or jwks_uri"
                : null;
    }

    /// <summary>
    /// Validates an optional human-facing client URI (<c>logo_uri</c>, <c>policy_uri</c>, <c>tos_uri</c>,
    /// <c>client_uri</c>). The value is null when omitted; otherwise it must be an absolute <c>http(s)</c>
    /// URI because the value is rendered to end users (a <c>javascript:</c> or <c>data:</c> URI would be a
    /// stored XSS vector).
    /// </summary>
    /// <returns>An error description, or <c>null</c> when acceptable.</returns>
    public static string? ValidateOptionalWebUri(string parameterName, string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{parameterName} must be an absolute http(s) URI";
        }

        return null;
    }

    /// <summary>
    /// RFC 7591 / OIDC Core 4: <c>initiate_login_uri</c> must be an absolute HTTPS URL where a third party
    /// may initiate a login for the client.
    /// </summary>
    /// <returns>An error description, or <c>null</c> when acceptable.</returns>
    public static string? ValidateInitiateLoginUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "initiate_login_uri must be an absolute HTTPS URI";
        }

        return null;
    }

    /// <summary>
    /// OIDC Core 6.3 / RFC 7591: <c>request_object_signing_alg</c> must be one of the algorithms the OP
    /// advertises in <c>request_object_signing_alg_values_supported</c>. <c>none</c> is only valid when
    /// the OP advertises it (this OP signs all request objects).
    /// </summary>
    /// <returns>An error description, or <c>null</c> when acceptable.</returns>
    public static string? ValidateRequestObjectSigningAlg(string? alg, IEnumerable<string> supportedAlgorithms)
    {
        if (string.IsNullOrWhiteSpace(alg))
        {
            return null;
        }

        return supportedAlgorithms.Contains(alg, StringComparer.Ordinal)
            ? null
            : $"request_object_signing_alg must be one of: {string.Join(", ", supportedAlgorithms)}";
    }

    /// <summary>
    /// RFC 7591 / RFC 9101 §5.5: a <c>request_uri</c> the client pre-registers must be an absolute URI the
    /// OP can fetch. Per OIDC Core 6.2.1 the URI MAY carry a hash fragment (it is stripped before fetching),
    /// so fragments are allowed here. Only https is fetchable by the OP's SSRF-safe client (http loopback is
    /// accepted for development deployments, mirroring the <c>jwks_uri</c> policy).
    /// </summary>
    /// <returns>An error description, or <c>null</c> when acceptable.</returns>
    public static string? ValidateRequestUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return $"Invalid request_uri: {uri}";
        }

        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && parsed.IsLoopback)
        {
            return null;
        }

        return $"request_uri scheme '{parsed.Scheme}' is not allowed";
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
