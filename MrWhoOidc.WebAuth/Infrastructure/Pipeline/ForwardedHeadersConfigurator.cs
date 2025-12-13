using System;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Infrastructure.Pipeline;

internal static class ForwardedHeadersConfigurator
{
    public static bool TryBuild(IConfiguration configuration, IHostEnvironment environment, ILogger logger, out ForwardedHeadersOptions options)
    {
        options = new ForwardedHeadersOptions();

        var forwardedEnabled = configuration.GetValue<bool?>("ForwardedHeaders:Enabled") ?? true;
        if (!forwardedEnabled)
        {
            return false;
        }

        options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            // Some hosting providers (including certain proxy chains) can emit a different number of values
            // for X-Forwarded-For vs X-Forwarded-Proto (e.g., multiple hops for client IP but a single proto).
            // When symmetry is required, ASP.NET Core ignores the forwarded headers entirely, which can
            // break HTTPS-dependent features like secure antiforgery cookies.
            RequireHeaderSymmetry = configuration.GetValue<bool?>("ForwardedHeaders:RequireHeaderSymmetry") ?? false,
            ForwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1
        };

        // Optional host allow-list (recommended when honoring X-Forwarded-Host)
        var allowedHosts = configuration.GetSection("ForwardedHeaders:AllowedHosts").Get<string[]>() ?? Array.Empty<string>();
        foreach (var h in allowedHosts)
        {
            if (!string.IsNullOrWhiteSpace(h)) options.AllowedHosts.Add(h);
        }

        // If not configured, default to issuer host allow-list when issuer is present.
        if (options.AllowedHosts.Count == 0)
        {
            var issuer = configuration["Oidc:Issuer"];
            if (Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) && !string.IsNullOrWhiteSpace(issuerUri.Host))
            {
                options.AllowedHosts.Add(issuerUri.Host);
            }
        }

        // Allow explicit proxy/network configuration.
        var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
        foreach (var p in knownProxies)
        {
            if (IPAddress.TryParse(p, out var ip)) options.KnownProxies.Add(ip);
            else if (!string.IsNullOrWhiteSpace(p)) logger.LogWarning("Invalid ForwardedHeaders:KnownProxies entry '{Proxy}'", p);
        }

        var knownNetworks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
        foreach (var n in knownNetworks)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out var prefix))
            {
                try
                {
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(ip, prefix));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Invalid ForwardedHeaders:KnownNetworks entry '{Network}'", n);
                }
            }
            else
            {
                logger.LogWarning("Invalid ForwardedHeaders:KnownNetworks entry '{Network}'", n);
            }
        }

        // Escape hatch (unsafe): trust forwarded headers from any proxy.
        // This is sometimes required on managed hosting where proxy IP ranges are not stable/knowable.
        // SECURITY NOTE: Only enable when the app is not directly reachable by clients (i.e., always behind a trusted reverse proxy).
        var unsafeTrustAll = configuration.GetValue<bool>("ForwardedHeaders:UnsafeTrustAll")
                             || configuration.GetValue<bool>("Testing:UnsafeTrustAllForwardedHeaders");
        if (unsafeTrustAll)
        {
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();

            logger.LogWarning(
                "Forwarded headers configured to trust all proxies (ForwardedHeaders:UnsafeTrustAll=true). " +
                "This can be unsafe if clients can reach the app directly. Environment={EnvironmentName}.",
                environment.EnvironmentName);

            if (options.AllowedHosts.Count == 0)
            {
                logger.LogWarning(
                    "ForwardedHeaders:UnsafeTrustAll is enabled but no ForwardedHeaders:AllowedHosts (or Oidc:Issuer host) is configured. " +
                    "Consider setting ForwardedHeaders:AllowedHosts to reduce host header spoofing risk.");
            }
        }

        return true;
    }
}
