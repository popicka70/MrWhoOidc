using System.Text.RegularExpressions;

namespace MrWhoOidc.Auth.MultiTenancy;

/// <summary>
/// Validation helpers for tenant slugs. Slugs are URL path segments (e.g. /t/{slug})
/// and feed into issuer URIs and tenant lookups, so they are restricted to a safe,
/// predictable character set.
/// </summary>
public static partial class TenantSlug
{
    /// <summary>Maximum slug length.</summary>
    public const int MaxLength = 63;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    /// <summary>
    /// Returns true if the slug consists only of lowercase letters, digits and hyphens,
    /// does not start or end with a hyphen, and is between 1 and <see cref="MaxLength"/> characters.
    /// </summary>
    public static bool IsValid(string? slug)
        => !string.IsNullOrEmpty(slug) && slug.Length <= MaxLength && SlugPattern().IsMatch(slug);
}
