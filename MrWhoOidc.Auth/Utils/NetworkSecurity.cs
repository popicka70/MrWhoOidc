using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Utils;

/// <summary>
/// Utility for network security checks, primarily to prevent SSRF (Server-Side Request Forgery).
/// </summary>
public static class NetworkSecurity
{
    /// <summary>
    /// Creates a safe SocketsHttpHandler that prevents SSRF by validating IP addresses at connection time.
    /// Redirects are not followed automatically; callers must validate any Location target before fetching it.
    /// </summary>
    public static SocketsHttpHandler CreateSafeHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        // A proxy (e.g. from HTTP_PROXY/HTTPS_PROXY) would make ConnectCallback validate the proxy's
        // address instead of the real target, letting the proxy reach internal hosts on our behalf.
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var ips = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

            var ip = ips.FirstOrDefault(i => !IsInternal(i));
            if (ip == null)
            {
                throw new InvalidOperationException($"No safe IP address found for host '{host}'.");
            }

            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;

            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    /// <summary>
    /// Creates a safe HttpClient that prevents SSRF by validating IP addresses at connection time.
    /// Redirects are not followed automatically; callers must validate any Location target before fetching it.
    /// </summary>
    public static HttpClient CreateSafeHttpClient(TimeSpan timeout)
    {
        var handler = CreateSafeHandler();

        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// Checks if an IP address is not publicly routable unicast: loopback, unspecified, private, link-local,
    /// carrier-grade NAT, benchmarking, documentation, multicast, reserved/broadcast, or an IPv6 address that
    /// embeds such an IPv4 address (IPv4-mapped/-compatible, NAT64, 6to4). Teredo is blocked entirely.
    /// </summary>
    /// <param name="ip">The IP address to check.</param>
    /// <returns>True if the address is internal; otherwise, false.</returns>
    public static bool IsInternal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsInternalIPv4(ip.GetAddressBytes());
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // :: (unspecified) — on most OSes connecting to it is treated as loopback.
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;   // fec0::/10 (deprecated site-local)
            if (ip.IsIPv6UniqueLocal) return true; // fc00::/7
            if (ip.IsIPv6Multicast) return true;   // ff00::/8

            var b = ip.GetAddressBytes();

            // IPv4-mapped (::ffff:a.b.c.d) and IPv4-compatible (::a.b.c.d, deprecated) addresses.
            if (ip.IsIPv4MappedToIPv6 || IsAllZero(b, 0, 12))
            {
                return IsInternalIPv4(b[12..16]);
            }

            // NAT64 well-known prefix 64:ff9b::/96 (RFC 6052) — embedded IPv4 in the last 32 bits.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && IsAllZero(b, 4, 8))
            {
                return IsInternalIPv4(b[12..16]);
            }

            // Local-use NAT64 prefix 64:ff9b:1::/48 (RFC 8215) — translator-specific, treat as internal.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01) return true;

            // 6to4 2002::/16 (RFC 3056) — embedded IPv4 in bits 16..47.
            if (b[0] == 0x20 && b[1] == 0x02)
            {
                return IsInternalIPv4(b[2..6]);
            }

            // Teredo 2001::/32 (RFC 4380) — tunnels to an obfuscated IPv4 endpoint; block outright.
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true;

            // Documentation 2001:db8::/32 (RFC 3849).
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true;
        }

        return false;
    }

    private static bool IsInternalIPv4(byte[] bytes)
    {
        // 0.0.0.0/8 "this network" — 0.x.x.x is routed to the local host on many OSes.
        if (bytes[0] == 0) return true;
        // 127.0.0.0/8 loopback
        if (bytes[0] == 127) return true;
        // RFC 1918: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // RFC 3927: link-local 169.254.0.0/16 (includes cloud metadata endpoints)
        if (bytes[0] == 169 && bytes[1] == 254) return true;
        // RFC 6598: shared address space (carrier-grade NAT) 100.64.0.0/10
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        // RFC 6890: IETF protocol assignments 192.0.0.0/24
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) return true;
        // RFC 5737: documentation 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return true;
        if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;
        if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;
        // RFC 2544: benchmarking 198.18.0.0/15
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;
        // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved (includes 255.255.255.255 broadcast)
        if (bytes[0] >= 224) return true;

        return false;
    }

    private static bool IsAllZero(byte[] bytes, int offset, int count)
    {
        for (var i = offset; i < offset + count; i++)
        {
            if (bytes[i] != 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Validates if a URI is safe to fetch from the server.
    /// Checks for allowed schemes (http/https) and ensures the host does not resolve to an internal IP.
    /// </summary>
    /// <param name="uriString">The URI string to validate.</param>
    /// <returns>True if the URI is considered safe; otherwise, false.</returns>
    public static async Task<bool> IsSafeUriAsync(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return false;
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return false;
        return await IsSafeUriAsync(uri);
    }

    /// <summary>
    /// Validates if a URI is safe to fetch from the server.
    /// Checks for allowed schemes (http/https) and ensures the host does not resolve to an internal IP.
    /// </summary>
    /// <param name="uri">The URI to validate.</param>
    /// <returns>True if the URI is considered safe; otherwise, false.</returns>
    public static async Task<bool> IsSafeUriAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;

        // Only allow http and https
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host)) return false;

        // If host is an IP address, check it directly
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            return !IsInternal(ip);
        }

        // Resolve hostname to IP addresses and check each one
        try
        {
            var ips = await Dns.GetHostAddressesAsync(uri.Host);
            if (ips.Length == 0) return false;

            // If any resolved IP is internal, consider the URI unsafe
            return ips.All(i => !IsInternal(i));
        }
        catch
        {
            // DNS resolution failure
            return false;
        }
    }
}
