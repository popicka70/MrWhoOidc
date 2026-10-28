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

    /// <summary>
    /// M-Path: the export file name comes from the server's <c>Content-Disposition</c> header, so it is
    /// attacker-controlled. It must never be able to escape the output directory, therefore it is reduced to a
    /// plain file name: any directory part is dropped and every character outside <c>[A-Za-z0-9._-]</c> is
    /// removed. Leading dots are stripped so neither ".." nor a dotfile can be produced.
    /// </summary>
    public static string SanitizeFileName(string? suggestedFileName, string fallback = "export.json")
    {
        if (string.IsNullOrWhiteSpace(suggestedFileName))
        {
            return fallback;
        }

        // Drops any directory component ("../../etc/passwd" -> "passwd", "/etc/passwd" -> "passwd").
        var name = Path.GetFileName(suggestedFileName.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return fallback;
        }

        var safe = new string(name
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            .ToArray())
            .TrimStart('.');

        if (string.IsNullOrWhiteSpace(safe))
        {
            return fallback;
        }

        return safe;
    }

    public static string ResolveOutputPath(string suggestedFileName, string? outputPath = null)
    {
        var safeName = SanitizeFileName(suggestedFileName);

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var exportsDir = Path.GetFullPath(GetDefaultExportsDirectory());
            var candidate = Path.GetFullPath(Path.Combine(exportsDir, safeName));
            if (!IsInsideDirectory(candidate, exportsDir))
            {
                throw new InvalidOperationException(
                    $"The suggested file name '{suggestedFileName}' resolves outside the exports directory.");
            }

            return candidate;
        }

        var trimmed = outputPath.Trim();
        var fullPath = Path.GetFullPath(trimmed);

        if (Directory.Exists(fullPath) || trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return Path.Combine(fullPath, safeName);
        }

        return fullPath;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the directory itself or lives inside it.
    /// </summary>
    private static bool IsInsideDirectory(string candidate, string directory)
    {
        var normalized = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalized, StringComparison.Ordinal);
    }
}
