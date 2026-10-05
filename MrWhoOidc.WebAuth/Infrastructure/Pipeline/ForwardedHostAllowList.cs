using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Infrastructure.Pipeline;

/// <summary>
/// Reads <c>ForwardedHeaders:AllowedHosts</c>. A bare <c>"*"</c> entry disables host validation entirely, so it is
/// only honored when <c>ForwardedHeaders:AllowAnyHost=true</c> is set explicitly; otherwise it is dropped (and the
/// usual PublicBaseUrl/Issuer fallback applies). Either way a warning is logged.
/// </summary>
internal static class ForwardedHostAllowList
{
    internal const string AllowAnyHostKey = "ForwardedHeaders:AllowAnyHost";

    internal static string[] ReadConfiguredHosts(IConfiguration configuration, ILogger? logger)
    {
        var hosts = (configuration.GetSection("ForwardedHeaders:AllowedHosts").Get<string[]>() ?? Array.Empty<string>())
            .Select(static x => x?.Trim())
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x!)
            .ToArray();

        if (!hosts.Contains("*", StringComparer.Ordinal))
        {
            return hosts;
        }

        if (configuration.GetValue<bool>(AllowAnyHostKey))
        {
            logger?.LogWarning(
                "ForwardedHeaders:AllowedHosts contains '*' and {Key}=true: host validation is disabled. " +
                "Only use this behind a proxy that validates the Host header.",
                AllowAnyHostKey);
            return hosts;
        }

        logger?.LogWarning(
            "Ignoring '*' in ForwardedHeaders:AllowedHosts because {Key} is not set to true; " +
            "the remaining entries (or the Oidc:PublicBaseUrl/Oidc:Issuer host) are enforced instead.",
            AllowAnyHostKey);
        return hosts.Where(static h => !string.Equals(h, "*", StringComparison.Ordinal)).ToArray();
    }
}
