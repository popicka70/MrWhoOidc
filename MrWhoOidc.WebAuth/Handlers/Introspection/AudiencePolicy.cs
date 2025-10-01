using System.Text.Json;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Enforces audience-based access control for introspection requests.
/// </summary>
public sealed class AudiencePolicy(IOptions<AuthOptions> authOptions)
{
    public bool IsClientAllowedForAudience(Client client, string? audience)
    {
        // If no audience specified, allow
        if (string.IsNullOrEmpty(audience))
        {
            return true;
        }

        // Check per-client allow-list from database
        if (!string.IsNullOrEmpty(client.IntrospectionAudiencesJson))
        {
            try
            {
                var clientAllowedAudiences = JsonSerializer.Deserialize<string[]>(client.IntrospectionAudiencesJson) 
                    ?? Array.Empty<string>();
                return clientAllowedAudiences.Contains(audience, StringComparer.Ordinal);
            }
            catch
            {
                // Fall through to global configuration
            }
        }

        // Check global configuration
        var permissions = authOptions.Value.IntrospectionPermissions;
        if (permissions is null || permissions.Count == 0)
        {
            return true; // No policy configured, allow all
        }

        if (!permissions.TryGetValue(client.ClientId, out var allowedAudiences))
        {
            return false; // Client not in allowlist
        }

        return allowedAudiences.Contains(audience, StringComparer.Ordinal);
    }
}
