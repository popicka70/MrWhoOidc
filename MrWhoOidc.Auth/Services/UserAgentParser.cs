using System.Text.RegularExpressions;

namespace MrWhoOidc.Auth.Services;

public interface IUserAgentParser
{
    UserAgentInfo Parse(string? userAgent);
}

public class UserAgentInfo
{
    public string Browser { get; set; } = "Unknown";
    public string Os { get; set; } = "Unknown";
    public string DeviceType { get; set; } = "desktop"; // desktop, mobile, tablet
    public string Icon { get; set; } = "bi-display"; // Bootstrap icon class
}

/// <summary>
/// Parses User-Agent strings to extract browser, OS, and device type information.
/// Uses simple regex patterns for common browsers and platforms.
/// </summary>
public sealed partial class UserAgentParser : IUserAgentParser
{
    public UserAgentInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return new UserAgentInfo();

        var info = new UserAgentInfo();

        // Detect device type first
        if (IsMobile(userAgent))
        {
            info.DeviceType = "mobile";
            info.Icon = "bi-phone";
        }
        else if (IsTablet(userAgent))
        {
            info.DeviceType = "tablet";
            info.Icon = "bi-tablet";
        }

        // Detect browser
        info.Browser = DetectBrowser(userAgent);

        // Detect OS
        info.Os = DetectOs(userAgent);

        return info;
    }

    private static bool IsMobile(string ua)
    {
        return MobileRegex().IsMatch(ua);
    }

    private static bool IsTablet(string ua)
    {
        return TabletRegex().IsMatch(ua) && !MobileRegex().IsMatch(ua);
    }

    private static string DetectBrowser(string ua)
    {
        // Order matters: check most specific first
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase))
            return "Edge";
        if (ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) || ua.Contains("Opera/", StringComparison.OrdinalIgnoreCase))
            return "Opera";
        if (ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase))
            return "Chrome";
        if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase))
            return "Safari";
        if (ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase))
            return "Firefox";
        if (ua.Contains("MSIE ", StringComparison.OrdinalIgnoreCase) || ua.Contains("Trident/", StringComparison.OrdinalIgnoreCase))
            return "Internet Explorer";

        return "Unknown";
    }

    private static string DetectOs(string ua)
    {
        // Check mobile OS first
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return "Android";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase))
            return "iOS";
        
        // Desktop OS
        if (ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase))
            return "Windows";
        if (ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
            return "macOS";
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return "Linux";

        return "Unknown";
    }

    [GeneratedRegex(@"Mobile|Android|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini", RegexOptions.IgnoreCase)]
    private static partial Regex MobileRegex();

    [GeneratedRegex(@"iPad|Android.*Tablet|Tablet|PlayBook", RegexOptions.IgnoreCase)]
    private static partial Regex TabletRegex();
}
