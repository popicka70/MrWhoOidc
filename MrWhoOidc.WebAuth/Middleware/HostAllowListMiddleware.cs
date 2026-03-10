using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Middleware;

public sealed class HostAllowListMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<HostAllowListMiddleware> _logger;

    public HostAllowListMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<HostAllowListMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var enforce = _configuration.GetValue<bool>("ForwardedHeaders:EnforceHostAllowList");
        if (!enforce)
        {
            await _next(context);
            return;
        }

        var allowed = _configuration.GetSection("ForwardedHeaders:AllowedHosts").Get<string[]>() ?? Array.Empty<string>();
        var allowedHosts = allowed
            .Select(static x => x?.Trim())
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x!)
            .ToArray();

        // Fallback to configured canonical host if no explicit allow-list.
        if (allowedHosts.Length == 0)
        {
            allowedHosts = GetHostFromUri(_configuration["Oidc:PublicBaseUrl"])
                ?? GetHostFromUri(_configuration["Oidc:Issuer"])
                ?? Array.Empty<string>();
        }

        // If enforcement is explicitly enabled but no hosts are configured, block all requests
        // rather than failing open. An empty allow-list with enforcement enabled is a
        // configuration error that should not silently permit all traffic.
        if (allowedHosts.Length == 0)
        {
            _logger.LogError(
                "ForwardedHeaders:EnforceHostAllowList is enabled but no allowed hosts could be resolved " +
                "(ForwardedHeaders:AllowedHosts / Oidc:PublicBaseUrl / Oidc:Issuer). " +
                "Blocking all requests until the configuration is corrected.");

            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await context.Response.WriteAsync("Misdirected Request: host allow-list enforcement is active but no hosts are configured.");
            return;
        }

        var host = context.Request.Host.Host;
        if (!IsAllowedHost(host, allowedHosts))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Bad Request");
            return;
        }

        await _next(context);
    }

    private static string[]? GetHostFromUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (string.IsNullOrWhiteSpace(uri.Host)) return null;
        return new[] { uri.Host };
    }

    private static bool IsAllowedHost(string host, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        foreach (var allowed in allowedHosts)
        {
            if (string.Equals(allowed, "*", StringComparison.Ordinal))
            {
                return true;
            }

            // Allow entries that include an optional port (we ignore ports in Request.Host.Host).
            var allowedHost = allowed;
            var colon = allowedHost.IndexOf(':');
            if (colon > 0)
            {
                allowedHost = allowedHost.Substring(0, colon);
            }

            if (string.Equals(host, allowedHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Wildcard support for subdomains (e.g., "*.example.com").
            if (allowedHost.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = allowedHost.Substring(1); // keep leading '.'
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
