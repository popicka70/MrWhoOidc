using System.Text;

namespace MrWhoOidc.Cli.Services;

/// <summary>
/// Writes files that hold secrets (tokens, client secrets, passwords) so that on Unix they are
/// owner-only (0600) from the moment they are created, instead of being created with the process
/// umask and tightened afterwards (which leaves a window where another user can open them).
/// </summary>
/// <remarks>
/// On Windows the file inherits the ACL of its directory (the user profile by default); the CLI config
/// additionally DPAPI-protects its token fields (see CliTokenProtection).
/// </remarks>
public static class OwnerOnlyFile
{
    public const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Creates <paramref name="directory"/> (and missing parents) as 0700 on Unix.
    /// Existing directories are left unchanged.
    /// </summary>
    public static void EnsureDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, OwnerDirectory);
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>. When <paramref name="overwrite"/> is
    /// false the call fails if the file exists (checked atomically by the OS). When true, the content is
    /// written to an owner-only temporary file in the same directory and moved over the target, so the
    /// result is owner-only even if the previous file had broader permissions.
    /// </summary>
    public static async Task WriteAllTextAsync(string path, string content, bool overwrite, CancellationToken ct = default)
    {
        if (!overwrite)
        {
            await WriteNewAsync(path, content, ct).ConfigureAwait(false);
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException($"Could not determine the directory for '{path}'.");
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteNewAsync(tempPath, content, ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task WriteNewAsync(string path, string content, CancellationToken ct)
    {
        await using var stream = new FileStream(path, CreateNewOptions());
        await using var writer = new StreamWriter(stream, Utf8NoBom);
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="FileStreamOptions"/> for creating a new file that must not already exist,
    /// owner-only on Unix.
    /// </summary>
    public static FileStreamOptions CreateNewOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }

        return options;
    }
}
