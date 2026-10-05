using System.Text.Json;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// RFC 8707 resource indicators decide the access token audience, so a client may only target resources
/// the server knows: the tenant's <see cref="AuthOptions.ApiAudiences"/> plus the client's own allow-list
/// (<see cref="Client.M2MAllowedAudiencesJson"/>). Unknown resources are rejected with invalid_target.
/// </summary>
public static class ResourceIndicatorPolicy
{
    public static bool IsAllowed(Client client, IEnumerable<string>? apiAudiences, string resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return false;

        // ADR-0010: the admin API resource is gated on the client alone, whatever ApiAudiences or the client's
        // own allow-list say.
        if (string.Equals(resource, AdminApiAccess.Resource, StringComparison.Ordinal))
        {
            return AdminApiAccess.ClientMayObtain(client);
        }

        if (apiAudiences is not null && apiAudiences.Contains(resource, StringComparer.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(client.M2MAllowedAudiencesJson))
        {
            return false;
        }

        try
        {
            var perClient = JsonSerializer.Deserialize<string[]>(client.M2MAllowedAudiencesJson) ?? Array.Empty<string>();
            return perClient.Contains(resource, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false; // corrupt allow-list: fail closed
        }
    }
}
