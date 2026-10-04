using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.HttpOverrides;

namespace MrWhoOidc.WebAuth.Infrastructure.Pipeline;

/// <summary>
/// Trust rules for the client certificate a TLS-terminating proxy forwards in <see cref="HeaderName"/>.
/// The certificate is the client's mTLS credential (RFC 8705), so the header must only be honoured when
/// the TCP peer is a configured proxy; otherwise anyone able to reach the pod could claim any certificate.
/// </summary>
internal static class ForwardedClientCertificate
{
    public const string HeaderName = "X-Client-Cert";

    /// <summary>
    /// Removes the forwarded certificate header unless the direct peer is a trusted proxy. Must run before
    /// UseForwardedHeaders, which replaces RemoteIpAddress with the (spoofable) X-Forwarded-For value.
    /// </summary>
    public static IApplicationBuilder UseForwardedClientCertificateGuard(this IApplicationBuilder app, ForwardedHeadersOptions? trusted, bool trustAllProxies, ILogger logger)
    {
        return app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(HeaderName) &&
                !IsTrustedPeer(context.Connection.RemoteIpAddress, trusted, trustAllProxies))
            {
                context.Request.Headers.Remove(HeaderName);
                logger.LogWarning("Dropped {Header} from untrusted peer {Peer}", HeaderName, context.Connection.RemoteIpAddress);
            }

            await next(context);
        });
    }

    internal static bool IsTrustedPeer(IPAddress? peer, ForwardedHeadersOptions? trusted, bool trustAllProxies)
    {
        if (trustAllProxies) return true;
        if (peer is null) return false;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        if (IPAddress.IsLoopback(peer)) return true; // same-host proxy / sidecar

        if (trusted is null) return false;
        return trusted.KnownProxies.Any(p => p.Equals(peer))
            || trusted.KnownIPNetworks.Any(n => n.Contains(peer));
    }

    /// <summary>
    /// Parses the forwarded certificate. Accepts base64 DER (Envoy/Traefik style) and URL-encoded PEM
    /// (nginx <c>$ssl_client_escaped_cert</c>); returns null for anything else.
    /// </summary>
    internal static X509Certificate2? Parse(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;

        try
        {
            var value = headerValue.Trim();
            if (value.Contains("BEGIN", StringComparison.Ordinal) || value.Contains("%2D", StringComparison.OrdinalIgnoreCase))
            {
                return X509Certificate2.CreateFromPem(Uri.UnescapeDataString(value));
            }

            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
