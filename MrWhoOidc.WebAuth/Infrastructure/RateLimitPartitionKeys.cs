using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.MultiTenancy;

namespace MrWhoOidc.WebAuth.Infrastructure;

/// <summary>
/// Builds rate-limit partition keys for client-authenticated OAuth endpoints (/token, /introspect, /par, /revoke).
/// The client_id is unauthenticated at limiter time, so it is never used on its own: the key combines tenant,
/// client_id and caller IP so a caller elsewhere cannot exhaust another client's budget (in the same or another
/// tenant) just by sending its client_id. The composite is hashed to keep keys short and keep IPs out of Redis.
/// </summary>
internal static class RateLimitPartitionKeys
{
    /// <summary>HttpContext.Items slot holding the form <c>client_id</c> once a middleware has read the form asynchronously.</summary>
    internal const string FormClientIdItemKey = "__form_client_id";

    internal static string ForClient(HttpContext context, string? clientId)
    {
        var tenant = ResolveTenant(context);
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var composite = $"{tenant}|{(string.IsNullOrEmpty(clientId) ? "-" : clientId)}|{ip}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(composite));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the client_id from HTTP Basic credentials, or from the form when it was already read and stashed
    /// in <see cref="HttpContext.Items"/>. Never reads the request body (no sync-over-async in limiter partitions).
    /// </summary>
    internal static string? GetClientId(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            try
            {
                var raw = header["Basic ".Length..].Trim();
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                var idx = pair.IndexOf(':');
                if (idx > 0) return Uri.UnescapeDataString(pair[..idx]);
            }
            catch (FormatException)
            {
                // Malformed Basic header: fall through to the form value.
            }
        }

        return context.Items.TryGetValue(FormClientIdItemKey, out var cached) && cached is string cid && cid.Length > 0
            ? cid
            : null;
    }

    private static string ResolveTenant(HttpContext context)
    {
        var tenant = context.RequestServices?.GetService<ITenantAccessor>()?.CurrentTenant;
        if (tenant is not null)
        {
            return tenant.TenantId.ToString("N");
        }

        // Tenant resolution skipped or not registered: fall back to the /t/{slug}/ path prefix.
        var path = context.Request.Path.Value;
        if (path is not null && path.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
        {
            var end = path.IndexOf('/', 3);
            var slug = end > 3 ? path[3..end] : path[3..];
            if (slug.Length > 0) return slug.ToLowerInvariant();
        }

        return "-";
    }
}
