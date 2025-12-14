using System.Text.Json;
using System.Text.Json.Nodes;

namespace MrWhoOidc.Auth.IdentityProviders;

public static class OidcProviderConfigJsonMerger
{
    private static readonly HashSet<string> StandardKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authority",
        "DiscoveryUrl",
        "ClientId",
        "ClientSecret",
        "ResponseType",
        "Scopes",
        "UsePKCE",
        "UseJAR",
        "UsePAR",
        "RequestedAcrValues",
        "Prompt",
        "ResponseMode",
        "ClockSkewSeconds",
        "TokenValidation",
        "BackChannelLogout",
        "ExtraAuthParams"
    };

    public static bool TryExtractExtendedJson(
        string? existingJson,
        out string extendedJson,
        out string? error)
    {
        extendedJson = "{}";
        error = null;

        if (string.IsNullOrWhiteSpace(existingJson))
        {
            return true;
        }

        JsonObject root;
        try
        {
            var node = JsonNode.Parse(existingJson);
            if (node is not JsonObject obj)
            {
                error = "Existing config must be a JSON object.";
                return false;
            }
            root = obj;
        }
        catch (Exception ex)
        {
            error = $"Existing config is not valid JSON: {ex.Message}";
            return false;
        }

        var extended = new JsonObject();
        foreach (var kvp in root)
        {
            if (StandardKeys.Contains(kvp.Key))
            {
                continue;
            }

            extended[kvp.Key] = kvp.Value?.DeepClone();
        }

        extendedJson = extended.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return true;
    }

    public static bool TryMerge(
        string? existingJson,
        OidcProviderConfig standardConfig,
        string? extendedJson,
        bool overwriteClientSecret,
        out string mergedJson,
        out string? error)
    {
        mergedJson = string.Empty;
        error = null;

        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                var node = JsonNode.Parse(existingJson);
                if (node is not JsonObject obj)
                {
                    error = "Existing config must be a JSON object.";
                    return false;
                }
                root = obj;
            }
            catch (Exception ex)
            {
                error = $"Existing config is not valid JSON: {ex.Message}";
                return false;
            }
        }

        JsonObject? extendedObj = null;
        if (!string.IsNullOrWhiteSpace(extendedJson))
        {
            try
            {
                var node = JsonNode.Parse(extendedJson);
                if (node is not JsonObject obj)
                {
                    error = "Extended configuration must be a JSON object.";
                    return false;
                }

                foreach (var kvp in obj)
                {
                    if (StandardKeys.Contains(kvp.Key))
                    {
                        error = $"Extended configuration cannot include standard key '{kvp.Key}'. Use the form fields instead.";
                        return false;
                    }
                }

                extendedObj = obj;
            }
            catch (Exception ex)
            {
                error = $"Extended configuration is not valid JSON: {ex.Message}";
                return false;
            }
        }

        // Standard fields
        SetRequiredString(root, "Authority", standardConfig.Authority);
        SetRequiredString(root, "ClientId", standardConfig.ClientId);

        SetOptionalString(root, "DiscoveryUrl", standardConfig.DiscoveryUrl);

        if (overwriteClientSecret)
        {
            SetOptionalString(root, "ClientSecret", standardConfig.ClientSecret);
        }

        SetRequiredString(root, "ResponseType", string.IsNullOrWhiteSpace(standardConfig.ResponseType) ? "code" : standardConfig.ResponseType);

        var scopes = standardConfig.Scopes ?? Array.Empty<string>();
        SetArray(root, "Scopes", scopes);

        SetBool(root, "UsePKCE", standardConfig.UsePKCE);
        SetBool(root, "UseJAR", standardConfig.UseJAR);
        SetBool(root, "UsePAR", standardConfig.UsePAR);

        SetOptionalString(root, "RequestedAcrValues", standardConfig.RequestedAcrValues);
        SetOptionalString(root, "Prompt", standardConfig.Prompt);
        SetOptionalString(root, "ResponseMode", standardConfig.ResponseMode);

        SetInt(root, "ClockSkewSeconds", standardConfig.ClockSkewSeconds);

        var tokenValidation = new JsonObject
        {
            ["ValidateIssuer"] = standardConfig.TokenValidation?.ValidateIssuer ?? true,
            ["ValidateAudience"] = standardConfig.TokenValidation?.ValidateAudience ?? false,
            ["ValidateLifetime"] = standardConfig.TokenValidation?.ValidateLifetime ?? true
        };
        SetObject(root, "TokenValidation", tokenValidation);

        SetBool(root, "BackChannelLogout", standardConfig.BackChannelLogout);

        if (standardConfig.ExtraAuthParams is null || standardConfig.ExtraAuthParams.Count == 0)
        {
            Remove(root, "ExtraAuthParams");
        }
        else
        {
            var extra = new JsonObject();
            foreach (var (key, value) in standardConfig.ExtraAuthParams)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                extra[key] = value;
            }
            SetObject(root, "ExtraAuthParams", extra);
        }

        // Extended properties (non-standard keys)
        if (extendedObj is not null)
        {
            // When the user supplies extended JSON, treat it as authoritative for ALL non-standard keys.
            // This allows deletion of previously stored extended keys by providing an empty object: {}.
            RemoveAllNonStandardKeys(root);

            foreach (var kvp in extendedObj)
            {
                // Preserve key casing as supplied in extended JSON.
                root[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        mergedJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return true;
    }

    private static void SetRequiredString(JsonObject root, string key, string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        Remove(root, key);
        root[key] = trimmed;
    }

    private static void SetOptionalString(JsonObject root, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Remove(root, key);
            return;
        }
        Remove(root, key);
        root[key] = value.Trim();
    }

    private static void SetBool(JsonObject root, string key, bool value)
    {
        Remove(root, key);
        root[key] = value;
    }

    private static void SetInt(JsonObject root, string key, int value)
    {
        Remove(root, key);
        root[key] = value;
    }

    private static void SetArray(JsonObject root, string key, IEnumerable<string> values)
    {
        Remove(root, key);
        var arr = new JsonArray();
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            arr.Add(v);
        }
        root[key] = arr;
    }

    private static void SetObject(JsonObject root, string key, JsonObject value)
    {
        Remove(root, key);
        root[key] = value;
    }

    private static void Remove(JsonObject root, string key)
    {
        var match = root.Select(kvp => kvp.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            root.Remove(match);
        }
    }

    private static void RemoveAllNonStandardKeys(JsonObject root)
    {
        var keys = root.Select(kvp => kvp.Key).ToList();
        foreach (var key in keys)
        {
            if (!StandardKeys.Contains(key))
            {
                root.Remove(key);
            }
        }
    }
}
