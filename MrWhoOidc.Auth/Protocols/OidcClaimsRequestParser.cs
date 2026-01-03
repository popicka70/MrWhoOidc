using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MrWhoOidc.Auth.Protocols;

internal static class OidcClaimsRequestParser
{
    // Keep this conservative: claims requests can be abused to create very large payloads.
    public const int DefaultMaxBytes = 16 * 1024;

    public static bool TryNormalizeClaimsParameter(
        string raw,
        int maxBytes,
        out string normalizedJson,
        out string? errorDescription)
    {
        normalizedJson = string.Empty;
        errorDescription = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            errorDescription = "claims parameter is empty";
            return false;
        }

        // Size guard (approximate; good enough for protection)
        if (maxBytes > 0 && raw.Length * sizeof(char) > maxBytes)
        {
            errorDescription = $"claims parameter exceeds max size ({maxBytes} bytes)";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorDescription = "claims must be a JSON object";
                return false;
            }

            // Validate top-level keys and shapes; we only support standard OIDC keys.
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "id_token", StringComparison.Ordinal) &&
                    !string.Equals(prop.Name, "userinfo", StringComparison.Ordinal))
                {
                    errorDescription = $"Unsupported claims top-level member '{prop.Name}'. Only 'id_token' and 'userinfo' are supported.";
                    return false;
                }

                if (prop.Value.ValueKind != JsonValueKind.Object)
                {
                    errorDescription = $"claims.{prop.Name} must be a JSON object";
                    return false;
                }
            }

            // Normalize to deterministic JSON.
            normalizedJson = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return true;
        }
        catch (JsonException)
        {
            errorDescription = "claims parameter is not valid JSON";
            return false;
        }
    }

    public static (HashSet<string> idTokenClaims, HashSet<string> userInfoClaims, HashSet<string> essentialIdTokenClaims, HashSet<string> essentialUserInfoClaims)
        ExtractRequestedClaimNames(string? normalizedClaimsJson)
    {
        var idToken = new HashSet<string>(StringComparer.Ordinal);
        var userinfo = new HashSet<string>(StringComparer.Ordinal);
        var essentialId = new HashSet<string>(StringComparer.Ordinal);
        var essentialUi = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(normalizedClaimsJson))
        {
            return (idToken, userinfo, essentialId, essentialUi);
        }

        using var doc = JsonDocument.Parse(normalizedClaimsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (idToken, userinfo, essentialId, essentialUi);
        }

        void ReadClaimSet(string memberName, HashSet<string> target, HashSet<string> essentialTarget)
        {
            if (!doc.RootElement.TryGetProperty(memberName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var claimProp in obj.EnumerateObject())
            {
                // claimProp.Value may be null or an object with 'essential', 'value', 'values'.
                target.Add(claimProp.Name);

                if (claimProp.Value.ValueKind == JsonValueKind.Object &&
                    claimProp.Value.TryGetProperty("essential", out var essentialEl) &&
                    essentialEl.ValueKind == JsonValueKind.True)
                {
                    essentialTarget.Add(claimProp.Name);
                }
            }
        }

        ReadClaimSet("id_token", idToken, essentialId);
        ReadClaimSet("userinfo", userinfo, essentialUi);

        return (idToken, userinfo, essentialId, essentialUi);
    }
}
