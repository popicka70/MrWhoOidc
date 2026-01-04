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

                // Validate claim member shapes.
                foreach (var claimProp in prop.Value.EnumerateObject())
                {
                    // Each claim entry can be: null, or an object with optional { essential, value, values }.
                    if (claimProp.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (claimProp.Value.ValueKind != JsonValueKind.Object)
                    {
                        errorDescription = $"claims.{prop.Name}.{claimProp.Name} must be null or a JSON object";
                        return false;
                    }

                    foreach (var member in claimProp.Value.EnumerateObject())
                    {
                        if (!string.Equals(member.Name, "essential", StringComparison.Ordinal) &&
                            !string.Equals(member.Name, "value", StringComparison.Ordinal) &&
                            !string.Equals(member.Name, "values", StringComparison.Ordinal))
                        {
                            errorDescription = $"Unsupported member claims.{prop.Name}.{claimProp.Name}.{member.Name}. Only 'essential', 'value', 'values' are supported.";
                            return false;
                        }

                        if (string.Equals(member.Name, "essential", StringComparison.Ordinal) &&
                            member.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            errorDescription = $"claims.{prop.Name}.{claimProp.Name}.essential must be a boolean";
                            return false;
                        }

                        if (string.Equals(member.Name, "values", StringComparison.Ordinal) &&
                            member.Value.ValueKind != JsonValueKind.Array)
                        {
                            errorDescription = $"claims.{prop.Name}.{claimProp.Name}.values must be an array";
                            return false;
                        }
                    }
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

    public sealed record ClaimConstraint(bool Essential, string? Value, string[]? Values);

    public static (Dictionary<string, ClaimConstraint> idToken, Dictionary<string, ClaimConstraint> userinfo)
        ExtractClaimConstraints(string? normalizedClaimsJson)
    {
        var idToken = new Dictionary<string, ClaimConstraint>(StringComparer.Ordinal);
        var userinfo = new Dictionary<string, ClaimConstraint>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(normalizedClaimsJson))
        {
            return (idToken, userinfo);
        }

        using var doc = JsonDocument.Parse(normalizedClaimsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (idToken, userinfo);
        }

        static string? ScalarToString(JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        static void ReadConstraints(string memberName, JsonElement root, Dictionary<string, ClaimConstraint> target)
        {
            if (!root.TryGetProperty(memberName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var claimProp in obj.EnumerateObject())
            {
                var essential = false;
                string? value = null;
                string[]? values = null;

                if (claimProp.Value.ValueKind == JsonValueKind.Object)
                {
                    if (claimProp.Value.TryGetProperty("essential", out var e) && e.ValueKind == JsonValueKind.True)
                    {
                        essential = true;
                    }

                    if (claimProp.Value.TryGetProperty("value", out var v))
                    {
                        value = ScalarToString(v);
                    }

                    if (claimProp.Value.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var item in vs.EnumerateArray())
                        {
                            var s = ScalarToString(item);
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                        }
                        values = list.Count > 0 ? list.ToArray() : null;
                    }
                }
                else if (claimProp.Value.ValueKind == JsonValueKind.Null)
                {
                    // No constraints.
                }
                else
                {
                    // Unexpected shapes should have been rejected during normalization; ignore defensively.
                }

                target[claimProp.Name] = new ClaimConstraint(essential, value, values);
            }
        }

        ReadConstraints("id_token", doc.RootElement, idToken);
        ReadConstraints("userinfo", doc.RootElement, userinfo);

        return (idToken, userinfo);
    }
}
