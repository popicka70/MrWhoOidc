using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Services;

internal sealed record RuntimeVersionPayload(
    string Service,
    string Environment,
    string Version,
    string InformationalVersion,
    string? Commit,
    string? Branch,
    string? RepoSlug,
    string? ServiceName);

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
            version.Commit,
            version.Branch,
            version.RepoSlug,
            version.ServiceName);
    }

    public static void ApplyResponseHeaders(HttpResponse response)
    {
        var version = Current;
        response.Headers["X-MrWhoOidc-Version"] = version.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(version.Commit))
        {
            response.Headers["X-MrWhoOidc-Commit"] = version.Commit;
        }

        if (!string.IsNullOrWhiteSpace(version.Branch))
        {
            response.Headers["X-MrWhoOidc-Branch"] = version.Branch;
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
        string? Commit,
        string? Branch,
        string? RepoSlug,
        string? ServiceName)
    {
        public static RuntimeVersionInfo FromAssembly(Assembly assembly)
        {
            var service = assembly.GetName().Name ?? "unknown";
            var assemblyVersion = assembly.GetName().Version?.ToString();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            return FromMetadata(
                service,
                assemblyVersion,
                fileVersion,
                informationalVersion,
                Environment.GetEnvironmentVariable);
        }

        internal static RuntimeVersionInfo FromMetadata(
            string service,
            string? assemblyVersion,
            string? fileVersion,
            string? informationalVersion,
            Func<string, string?>? environmentVariableReader)
        {
            environmentVariableReader ??= Environment.GetEnvironmentVariable;

            var commitFallback = FirstNonEmpty(
                environmentVariableReader("RENDER_GIT_COMMIT"),
                environmentVariableReader("GITHUB_SHA"),
                environmentVariableReader("SOURCE_VERSION"),
                environmentVariableReader("COMMIT_SHA"),
                environmentVariableReader("GIT_COMMIT"));

            var branch = FirstNonEmpty(
                environmentVariableReader("RENDER_GIT_BRANCH"),
                environmentVariableReader("GITHUB_REF_NAME"),
                environmentVariableReader("BRANCH_NAME"));

            var repoSlug = FirstNonEmpty(
                environmentVariableReader("RENDER_GIT_REPO_SLUG"),
                environmentVariableReader("GITHUB_REPOSITORY"));

            var serviceName = FirstNonEmpty(
                environmentVariableReader("RENDER_SERVICE_NAME"),
                environmentVariableReader("RENDER_SERVICE_ID"));

            var normalizedInformationalVersion = NormalizeInformationalVersion(informationalVersion, fileVersion, assemblyVersion, commitFallback);
            var commit = ExtractCommit(normalizedInformationalVersion) ?? NormalizeValue(commitFallback);

            return new RuntimeVersionInfo(
                service,
                ExtractVersion(normalizedInformationalVersion),
                normalizedInformationalVersion,
                commit,
                NormalizeValue(branch),
                NormalizeValue(repoSlug),
                NormalizeValue(serviceName));
        }

        private static string NormalizeInformationalVersion(string? informationalVersion, string? fileVersion, string? assemblyVersion, string? commitFallback)
        {
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var normalized = informationalVersion.Trim();
                return ExtractCommit(normalized) is null && !string.IsNullOrWhiteSpace(commitFallback)
                    ? $"{normalized}+{commitFallback.Trim()}"
                    : normalized;
            }

            var baseVersion = string.Empty;
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                baseVersion = fileVersion.Trim();
            }
            else
            {
                baseVersion = string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion.Trim();
            }

            return !string.IsNullOrWhiteSpace(commitFallback)
                ? $"{baseVersion}+{commitFallback.Trim()}"
                : baseVersion;
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

        private static string? FirstNonEmpty(params string?[] values)
            => values.Select(NormalizeValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        private static string? NormalizeValue(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}