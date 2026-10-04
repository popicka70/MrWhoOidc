using System.Runtime.Versioning;
using MrWhoOidc.Cli.Configuration;

namespace MrWhoOidc.Cli.Services;

public static class CliFileOutput
{
    public static string GetDefaultExportsDirectory()
    {
        return Path.Combine(CliConfig.GetConfigDirectory(), "exports");
    }

    public static async Task<string> WriteTextAsync(
        string content,
        string suggestedFileName,
        string? outputPath = null,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        var resolvedPath = ResolveOutputPath(suggestedFileName, outputPath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Could not determine the directory for '{resolvedPath}'.");
        }

        OwnerOnlyFile.EnsureDirectory(directory);

        if (File.Exists(resolvedPath) && !overwrite)
        {
            throw AlreadyExists(resolvedPath);
        }

        try
        {
            // Created owner-only (0600 on Unix) from the start; no chmod-after-write window.
            await OwnerOnlyFile.WriteAllTextAsync(resolvedPath, content, overwrite, ct).ConfigureAwait(false);
        }
        catch (IOException) when (!overwrite && File.Exists(resolvedPath))
        {
            // Lost a race with another writer between the existence check and CreateNew.
            throw AlreadyExists(resolvedPath);
        }

        return resolvedPath;
    }

    private static InvalidOperationException AlreadyExists(string path) =>
        new($"The output file '{path}' already exists. Use --overwrite to replace it.");

    public static string ResolveOutputPath(string suggestedFileName, string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Path.Combine(GetDefaultExportsDirectory(), suggestedFileName);
        }

        var trimmed = outputPath.Trim();
        var fullPath = Path.GetFullPath(trimmed);

        if (Directory.Exists(fullPath) || trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return Path.Combine(fullPath, suggestedFileName);
        }

        return fullPath;
    }
}
