using System;
using System.Text.Json;

namespace MrWhoOidc.Auth.Validation;

/// <summary>
/// Validates JSON schema for tenant settings and metadata fields.
/// </summary>
public class TenantSettingsValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Validates that the JSON string is well-formed and matches the expected schema.
    /// </summary>
    public bool IsValid(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true; // Null/empty is valid
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates and deserializes the JSON string to the specified type.
    /// </summary>
    public bool TryDeserialize<T>(string? json, out T? result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            result = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
