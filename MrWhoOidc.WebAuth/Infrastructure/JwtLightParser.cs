using System;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Infrastructure;

/// <summary>
/// Lightweight, non-validating JWT helper functions used only for metrics/audit enrichment.
/// Does not verify signatures; do not use for security decisions.
/// </summary>
public static class JwtLightParser
{
    public static bool IsProbablyJwt(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        int dots = 0;
        foreach (var ch in token)
        {
            if (ch == '.') { dots++; if (dots >= 2) break; }
        }
        return dots >= 2;
    }

    public static string? TryGetAudience(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var json = DecodeBase64UrlToString(parts[1]);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("aud", out var audEl))
            {
                if (audEl.ValueKind == JsonValueKind.String) return audEl.GetString();
                if (audEl.ValueKind == JsonValueKind.Array && audEl.GetArrayLength() > 0) return audEl[0].GetString();
            }
        }
        catch { }
        return null;
    }

    public static string? TryGetClaim(string token, string claim)
    {
        if (string.IsNullOrEmpty(token) || token.Count(c => c == '.') != 2) return null;
        try
        {
            var parts = token.Split('.');
            var payload = DecodeBase64UrlToString(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty(claim, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch { }
        return null;
    }

    private static string DecodeBase64UrlToString(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
