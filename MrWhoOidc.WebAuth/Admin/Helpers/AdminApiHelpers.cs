using System.Text.Json;

namespace MrWhoOidc.WebAuth.Admin.Helpers;

internal static class AdminApiHelpers
{
    public static string CompactJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false });
    }

    public static string ComputeSha256Hex(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static (bool Ok, string Summary, string? Message, int KeyCount, int UniqueKidCount, List<string> DuplicateKids)? ComputeJwksStatus(string? jwksJson)
    {
        if (string.IsNullOrWhiteSpace(jwksJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jwksJson);
            var keys = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
            {
                keys = keysArr.EnumerateArray().ToList();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                keys.Add(doc.RootElement);
            }
            else
            {
                return (false, "Invalid", "JWKS must be an object with 'keys' array or a single JWK object.", 0, 0, new());
            }

            var count = keys.Count;
            var kids = keys.Select(k => k.TryGetProperty("kid", out var kid) ? kid.GetString() : null).ToList();
            var nonNullKids = kids.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var dup = nonNullKids.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key!).ToList();

            var ok = dup.Count == 0;
            var summary = ok ? "Valid JWKS" : "Duplicates";
            var msg = ok ? $"{count} key(s), {nonNullKids.Distinct(StringComparer.Ordinal).Count()} distinct kid" : $"Duplicate kid(s): {string.Join(", ", dup)}";
            return (ok, summary, msg, count, nonNullKids.Distinct(StringComparer.Ordinal).Count(), dup);
        }
        catch (Exception ex)
        {
            return (false, "Invalid", ex.Message, 0, 0, new());
        }
    }
}
