using System;

namespace MrWhoOidc.Auth.Licensing.Models;

public enum LicenseTier
{
    Community = 0,
    Professional = 1,
    Enterprise = 2,
    EnterprisePlus = 3
}

public static class LicenseTierExtensions
{
    public static string ToTierString(this LicenseTier tier) => tier switch
    {
        LicenseTier.Community => "community",
        LicenseTier.Professional => "professional",
        LicenseTier.Enterprise => "enterprise",
        LicenseTier.EnterprisePlus => "enterprise+",
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };

    public static LicenseTier FromTierString(string tierString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierString);
        return tierString.ToLowerInvariant() switch
        {
            "community" => LicenseTier.Community,
            "professional" => LicenseTier.Professional,
            "enterprise" => LicenseTier.Enterprise,
            "enterprise+" => LicenseTier.EnterprisePlus,
            _ => throw new ArgumentException($"Unknown license tier: {tierString}", nameof(tierString))
        };
    }
}
