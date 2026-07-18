namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// A site-wide UI style preset. Applied as a data-ui-preset attribute on the html element;
/// preset styles live in wwwroot/css/ui-presets.css.
/// </summary>
public sealed record UiPreset(string Key, string DisplayName, string Description);

/// <summary>
/// Registry of site-wide UI style presets. Unlike <see cref="StyleSchemes"/> (auth pages only),
/// these re-skin the whole application via CSS variables.
/// </summary>
public static class UiPresets
{
    public const string CookieName = "mrwho-ui-preset";
    public const string QueryParam = "ui-preset";
    public const string DefaultKey = "classic";

    public static readonly UiPreset Classic = new("classic", "Classic", "Teal identity, calm and modern");
    public static readonly UiPreset Paper = new("paper", "Paper", "Warm editorial look with serif headings");
    public static readonly UiPreset Slate = new("slate", "Slate", "Quiet corporate neutrals, light sidebar");
    public static readonly UiPreset Terminal = new("terminal", "Terminal", "Dark console look for late nights");
    public static readonly UiPreset Nord = new("nord", "Nord", "Arctic blue-grey palette, calm and readable");
    public static readonly UiPreset Solarized = new("solarized", "Solarized", "Warm low-contrast tones for eye comfort");
    public static readonly UiPreset Monochrome = new("monochrome", "Monochrome", "Pure grayscale professional, zero distraction");
    public static readonly UiPreset HighContrast = new("high-contrast", "High Contrast", "Accessibility-first, large text, strong borders");

    public static readonly UiPreset[] All = { Classic, Paper, Slate, Terminal, Nord, Solarized, Monochrome, HighContrast };

    public static UiPreset Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Classic;
        foreach (var preset in All)
        {
            if (string.Equals(preset.Key, key, StringComparison.OrdinalIgnoreCase))
                return preset;
        }
        return Classic;
    }

    /// <summary>
    /// Resolves the active preset for a request: ?ui-preset= query (handy for testing
    /// and screenshots) wins over the persisted cookie; unknown keys fall back to Classic.
    /// </summary>
    public static UiPreset Resolve(HttpContext? context)
    {
        if (context is null)
            return Classic;
        var fromQuery = (string?)context.Request.Query[QueryParam];
        if (!string.IsNullOrWhiteSpace(fromQuery))
            return Resolve(fromQuery);
        context.Request.Cookies.TryGetValue(CookieName, out var fromCookie);
        return Resolve(fromCookie);
    }
}
