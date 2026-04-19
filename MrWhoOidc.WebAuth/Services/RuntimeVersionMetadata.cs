using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Services;

internal sealed record RuntimeVersionPayload(
    string Service,
    string Environment,
    string Version,
    string InformationalVersion,
    string? Commit);

internal static class RuntimeVersionMetadata
{
    private static readonly Lazy<RuntimeVersionInfo> CurrentVersion = new(static () => RuntimeVersionInfo.FromAssembly(typeof(RuntimeVersionMetadata).Assembly));

    public static RuntimeVersionInfo Current => CurrentVersion.Value;

    public static RuntimeVersionPayload CreatePayload(string environment)
    {
        var version = Current;
        return new RuntimeVersionPayload(
            version.Service,
            environment,
            version.Version,
            version.InformationalVersion,
            version.Commit);
    }

    public static void ApplyResponseHeaders(HttpResponse response)
    {
        var version = Current;
        response.Headers["X-MrWhoOidc-Version"] = version.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(version.Commit))
        {
            response.Headers["X-MrWhoOidc-Commit"] = version.Commit;
        }
    }

    public static void ApplyNoStoreHeaders(HttpResponse response)
    {
        response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
    }

    internal sealed record RuntimeVersionInfo(
        string Service,
        string Version,
        string InformationalVersion,
        string? Commit)
    {
        public static RuntimeVersionInfo FromAssembly(Assembly assembly)
        {
            var service = assembly.GetName().Name ?? "unknown";
            var assemblyVersion = assembly.GetName().Version?.ToString();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            var normalizedInformationalVersion = NormalizeInformationalVersion(informationalVersion, fileVersion, assemblyVersion);

            return new RuntimeVersionInfo(
                service,
                ExtractVersion(normalizedInformationalVersion),
                normalizedInformationalVersion,
                ExtractCommit(normalizedInformationalVersion));
        }

        private static string NormalizeInformationalVersion(string? informationalVersion, string? fileVersion, string? assemblyVersion)
        {
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return informationalVersion;
            }

            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }

            return string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion;
        }

        private static string ExtractVersion(string informationalVersion)
        {
            var separatorIndex = informationalVersion.IndexOf('+');
            if (separatorIndex <= 0)
            {
                return informationalVersion;
            }

            return informationalVersion[..separatorIndex];
        }

        private static string? ExtractCommit(string informationalVersion)
        {
            var separatorIndex = informationalVersion.IndexOf('+');
            if (separatorIndex < 0 || separatorIndex == informationalVersion.Length - 1)
            {
                return null;
            }

            var commit = informationalVersion[(separatorIndex + 1)..].Trim();
            return string.IsNullOrWhiteSpace(commit) ? null : commit;
        }
    }
}