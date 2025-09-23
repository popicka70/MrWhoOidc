namespace MrWhoOidc.WebAuth.Services;

public sealed record StyleScheme(string Key, string DisplayName, string CssClass);

public static class StyleSchemes
{
    // Default key
    public const string DefaultKey = "classic";

    // First 5 schemes
    public static readonly StyleScheme Classic  = new("classic",  "Classic",  "scheme-classic");
    public static readonly StyleScheme Ocean    = new("ocean",    "Ocean (info)",    "scheme-ocean");
    public static readonly StyleScheme Forest   = new("forest",   "Forest (success)", "scheme-forest");
    public static readonly StyleScheme Plum     = new("plum",     "Plum",            "scheme-plum");
    public static readonly StyleScheme Contrast = new("contrast", "High contrast",    "scheme-contrast");

    public static readonly StyleScheme[] All = new[] { Classic, Ocean, Forest, Plum, Contrast };

    public static StyleScheme Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Classic;
        foreach (var s in All)
        {
            if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return Classic;
    }
}
