using System.Text;
using System.Text.RegularExpressions;

namespace MrWhoOidc.WebAuth.Extensions;

/// <summary>
/// Helper methods for converting URL paths between PascalCase and kebab-case conventions.
/// </summary>
public static class UrlConversionHelper
{
    /// <summary>
    /// Converts a PascalCase URL path to kebab-case.
    /// </summary>
    /// <param name="path">The path to convert (e.g., "/Admin/Providers/Edit").</param>
    /// <returns>The kebab-case path (e.g., "/admin/providers/edit").</returns>
    /// <example>
    /// <code>
    /// ToKebabCase("/Admin/Providers") // returns "/admin/providers"
    /// ToKebabCase("/Auth/External/Callback") // returns "/auth/external/callback"
    /// ToKebabCase("/PlatformAdmin/Settings") // returns "/platform-admin/settings"
    /// </code>
    /// </example>
    public static string ToKebabCase(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Split path into segments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();

        foreach (var segment in segments)
        {
            if (result.Length > 0)
                result.Append('/');

            // Convert PascalCase segment to kebab-case
            var kebabSegment = ConvertSegmentToKebabCase(segment);
            result.Append(kebabSegment);
        }

        // Preserve leading slash if original path had one
        if (path.StartsWith('/'))
            return "/" + result.ToString();

        return result.ToString();
    }

    /// <summary>
    /// Suggests a kebab-case alternative for a PascalCase path.
    /// Returns null if path is already kebab-case or doesn't contain PascalCase segments.
    /// </summary>
    /// <param name="path">The path to analyze.</param>
    /// <returns>The suggested kebab-case path, or null if no suggestion needed.</returns>
    /// <example>
    /// <code>
    /// SuggestKebabCase("/Admin/Providers") // returns "/admin/providers"
    /// SuggestKebabCase("/admin/providers") // returns null (already kebab-case)
    /// SuggestKebabCase("/Auth/QrMobile") // returns "/auth/qr-mobile"
    /// </code>
    /// </example>
    public static string? SuggestKebabCase(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Check if path contains any PascalCase segments
        if (!ContainsPascalCase(path))
            return null;

        // Convert to kebab-case
        var suggestion = ToKebabCase(path);

        // Only return suggestion if it differs from original
        return suggestion.Equals(path, StringComparison.OrdinalIgnoreCase) ? null : suggestion;
    }

    /// <summary>
    /// Checks if a path contains any PascalCase segments.
    /// </summary>
    private static bool ContainsPascalCase(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            // Skip segments that are already all lowercase (kebab-case or single word)
            if (segment == segment.ToLowerInvariant())
                continue;

            // Check if segment contains uppercase letters (PascalCase indicator)
            if (segment.Any(char.IsUpper))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Converts a single path segment from PascalCase to kebab-case.
    /// </summary>
    /// <example>
    /// <code>
    /// ConvertSegmentToKebabCase("Admin") // returns "admin"
    /// ConvertSegmentToKebabCase("PlatformAdmin") // returns "platform-admin"
    /// ConvertSegmentToKebabCase("QrMobile") // returns "qr-mobile"
    /// </code>
    /// </example>
    private static string ConvertSegmentToKebabCase(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return segment;

        // Insert hyphen before uppercase letters (except first character)
        // Example: "PlatformAdmin" -> "Platform-Admin"
        var withHyphens = Regex.Replace(segment, "(?<!^)([A-Z])", "-$1");

        // Convert to lowercase
        // Example: "Platform-Admin" -> "platform-admin"
        return withHyphens.ToLowerInvariant();
    }
}
