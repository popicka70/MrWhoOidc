using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.Auth.IdentityProviders;

/// <summary>
/// Member-level protection of the secrets inside <c>IdentityProvider.ConfigJson</c> (the upstream client secret).
/// The JSON stays valid and readable; only the secret members carry a <c>dp:v1:</c> value at rest. Protection is
/// applied by <c>AuthDbContext</c> on save and undone when an entity is materialized, so every reader of the config
/// (sign-in, token exchange, federated logout, export) sees the plaintext without knowing about it.
/// </summary>
public static class ProviderConfigSecrets
{
    /// <summary>Shown instead of a secret in admin views; sent back unchanged it means "keep the stored secret".</summary>
    public const string RedactedMarker = "<redacted>";

    private const string ProtectedPrefix = "dp:v1:";

    private static readonly HashSet<string> SecretMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClientSecret",
        "client_secret"
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string? Protect(string? configJson, ISecretProtector protector)
        => Transform(configJson, value => value.StartsWith(ProtectedPrefix, StringComparison.Ordinal) || value == RedactedMarker
            ? value
            : protector.ProtectProviderSecret(value));

    /// <summary>
    /// Unprotects the secret members. A value that cannot be unprotected (lost or rotated-out key ring) is left as
    /// stored: the upstream then rejects it, which is visible, instead of the whole provider failing to load.
    /// </summary>
    public static string? Unprotect(string? configJson, ISecretProtector protector)
        => Transform(configJson, value =>
        {
            if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                return value;
            }

            try
            {
                return protector.UnprotectProviderSecret(value);
            }
            catch (CryptographicException)
            {
                return value;
            }
        });

    public static string? Redact(string? configJson) => Transform(configJson, _ => RedactedMarker);

    /// <summary>True when a secret member is stored without protection (backfill candidate).</summary>
    public static bool HasUnprotectedSecret(string? configJson)
    {
        var found = false;
        Transform(configJson, value =>
        {
            found |= !value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
            return value;
        });
        return found;
    }

    /// <summary>
    /// For a config edited from a redacted view: a secret member still holding <see cref="RedactedMarker"/> takes the
    /// value from <paramref name="existingConfigJson"/> (dropped when there is none).
    /// </summary>
    public static string? RestoreRedacted(string? incomingConfigJson, string? existingConfigJson)
    {
        if (string.IsNullOrWhiteSpace(incomingConfigJson) || !incomingConfigJson.Contains(RedactedMarker, StringComparison.Ordinal))
        {
            return incomingConfigJson;
        }

        if (TryParseObject(incomingConfigJson) is not { } incoming)
        {
            return incomingConfigJson;
        }

        var existing = TryParseObject(existingConfigJson);
        var changed = false;
        foreach (var name in incoming.Select(p => p.Key).ToList())
        {
            if (!SecretMembers.Contains(name) || incoming[name] is not JsonValue value
                || !value.TryGetValue<string>(out var text) || text != RedactedMarker)
            {
                continue;
            }

            var stored = existing?.FirstOrDefault(p => SecretMembers.Contains(p.Key) && p.Value is JsonValue).Value;
            if (stored is null)
            {
                incoming.Remove(name);
            }
            else
            {
                incoming[name] = stored.DeepClone();
            }

            changed = true;
        }

        return changed ? incoming.ToJsonString(WriteOptions) : incomingConfigJson;
    }

    private static string? Transform(string? configJson, Func<string, string> transform)
    {
        if (string.IsNullOrWhiteSpace(configJson) || TryParseObject(configJson) is not { } root)
        {
            return configJson;
        }

        var changed = false;
        foreach (var name in root.Select(p => p.Key).ToList())
        {
            if (!SecretMembers.Contains(name) || root[name] is not JsonValue value
                || !value.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text))
            {
                continue;
            }

            var replaced = transform(text);
            if (!string.Equals(replaced, text, StringComparison.Ordinal))
            {
                root[name] = replaced;
                changed = true;
            }
        }

        return changed ? root.ToJsonString(WriteOptions) : configJson;
    }

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
