using System.Security.Claims;

namespace MrWhoOidc.KeyGen.Security;

/// <summary>
/// Formats the signed-in user for the audit columns <c>KeyPairMetadata.CreatedBy</c>,
/// <c>LicenseTokenMetadata.GeneratedBy</c> and <c>KeyDownloadRecord.DownloadedBy</c>.
/// </summary>
public static class IssuerIdentity
{
    /// <summary>Matches the <c>HasMaxLength(200)</c> of the audit columns.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Returns e.g. <c>Jane Admin &lt;jane@example.com&gt; (sub: 42)</c>, or <c>null</c> for an
    /// anonymous principal. The subject is always kept, since it is the stable identifier.
    /// </summary>
    public static string? Describe(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var name = user.FindFirst("name")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.Identity.Name;
        var email = user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value;

        var display = (name, email) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) when !string.Equals(name, email, StringComparison.OrdinalIgnoreCase) => $"{name} <{email}>",
            ({ Length: > 0 }, _) => name,
            (_, { Length: > 0 }) => email,
            _ => null
        };

        var subPart = string.IsNullOrEmpty(sub) ? null : $"(sub: {sub})";
        if (display is null)
        {
            return Truncate(subPart ?? "(unknown authenticated user)");
        }

        if (subPart is null)
        {
            return Truncate(display);
        }

        // Keep the subject intact and trim the display part if the total would exceed the column.
        var room = MaxLength - subPart.Length - 1;
        if (room <= 0)
        {
            return Truncate(subPart);
        }

        return $"{Truncate(display, room)} {subPart}";
    }

    private static string Truncate(string value, int max = MaxLength) =>
        value.Length <= max ? value : value[..max];
}
