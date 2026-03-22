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

        Directory.CreateDirectory(directory);

        if (File.Exists(resolvedPath) && !overwrite)
        {
            throw new InvalidOperationException($"The output file '{resolvedPath}' already exists. Use --overwrite to replace it.");
        }

        await File.WriteAllTextAsync(resolvedPath, content, ct).ConfigureAwait(false);
        SetOwnerOnlyPermissions(resolvedPath);
        return resolvedPath;
    }

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

    private static void SetOwnerOnlyPermissions(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort only.
        }
    }
}
