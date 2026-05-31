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
    /// Checks if an IP address is an internal address (loopback, link-local, or private range).
    /// </summary>
    /// <param name="ip">The IP address to check.</param>
    /// <returns>True if the address is internal; otherwise, false.</returns>
    public static bool IsInternal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();

            // RFC 1918: Private-Use Networks
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // RFC 3927: Link-Local
            // 169.254.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // RFC 6598: Shared Address Space (Carrier-grade NAT)
            // 100.64.0.0/10
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv6UniqueLocal) return true;

            // IPv4-mapped IPv6 addresses
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsInternal(ip.MapToIPv4());
            }
        }

        return false;
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
