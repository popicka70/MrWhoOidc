using System.Collections.Generic;

namespace MrWhoOidc.Auth.Options;

/// <summary>
/// Configuration options for public email domains.
/// These domains are treated as public/shared and may be restricted for tenant enrollment.
/// </summary>
public class PublicEmailDomainOptions
{
    /// <summary>
    /// List of public email domains that are restricted for tenant enrollment.
    /// Default: Common public email providers
    /// </summary>
    public HashSet<string> Domains { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "aol.com",
        "gmail.com",
        "googlemail.com",
        "gmx.com",
        "hotmail.com",
        "icloud.com",
        "live.com",
        "mac.com",
        "mail.com",
        "me.com",
        "msn.com",
        "outlook.com",
        "pm.me",
        "proton.me",
        "protonmail.com",
        "yahoo.com",
        "zoho.com"
    };
}
