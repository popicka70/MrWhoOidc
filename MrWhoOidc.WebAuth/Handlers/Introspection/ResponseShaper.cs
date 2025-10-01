using System.Text.Json;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Shapes introspection responses based on per-client privacy policies.
/// </summary>
public sealed class ResponseShaper(IOptions<AuthOptions> authOptions)
{
    public Dictionary<string, object?> ShapeResponse(
        Dictionary<string, object?> response,
        Client client)
    {
        var allowedFields = GetAllowedFields(client);

        // If no fields configured, return only active claim
        if (allowedFields.Length == 0)
        {
            return new Dictionary<string, object?>
            {
                ["active"] = response.TryGetValue("active", out var active) ? active : true
            };
        }

        // Filter response to allowed fields
        var shaped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in allowedFields)
        {
            if (response.TryGetValue(field, out var value))
            {
                shaped[field] = value;
            }
        }

        // Always preserve 'active' claim even if not in allowlist
        if (!shaped.ContainsKey("active") && response.TryGetValue("active", out var activeValue))
        {
            shaped["active"] = activeValue;
        }

        return shaped;
    }

    private string[] GetAllowedFields(Client client)
    {
        // Check per-client database configuration
        if (!string.IsNullOrEmpty(client.IntrospectionResponseFieldsJson))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(client.IntrospectionResponseFieldsJson)
                    ?? Array.Empty<string>();
            }
            catch
            {
                // Fall through to configuration
            }
        }

        var config = authOptions.Value;

        // Check per-client configuration
        if (config.IntrospectionResponseFields is { Count: > 0 } &&
            config.IntrospectionResponseFields.TryGetValue(client.ClientId, out var perClientFields))
        {
            return perClientFields;
        }

        // Use global default
        return config.IntrospectionDefaultResponseFields ?? Array.Empty<string>();
    }
}
