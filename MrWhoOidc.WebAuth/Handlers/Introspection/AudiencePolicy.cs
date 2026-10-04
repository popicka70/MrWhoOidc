using System.Text.Json;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Handlers.Introspection;

/// <summary>
/// Decides whether an authenticated caller may learn the contents of a token (RFC 7662 §4: the server
/// must determine whether the caller is authorized to introspect the given token).
/// Deny by default; a caller is allowed only when it is the token's client, one of its audiences, or
/// is explicitly granted one of the token's audiences per client or in global configuration.
/// </summary>
public sealed class AudiencePolicy(IOptions<AuthOptions> authOptions)
{
    public bool IsClientAllowed(Client caller, IReadOnlyCollection<string> tokenAudiences, string? tokenClientId)
    {
        if (!string.IsNullOrEmpty(tokenClientId) && string.Equals(tokenClientId, caller.ClientId, StringComparison.Ordinal))
        {
            return true;
        }

        if (tokenAudiences.Count == 0)
        {
            return false;
        }

        if (tokenAudiences.Contains(caller.ClientId, StringComparer.Ordinal))
        {
            return true;
        }

        var granted = GrantedAudiences(caller);
        return tokenAudiences.Any(a => granted.Contains(a, StringComparer.Ordinal));
    }

    private string[] GrantedAudiences(Client caller)
    {
        if (!string.IsNullOrEmpty(caller.IntrospectionAudiencesJson))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(caller.IntrospectionAudiencesJson) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                return Array.Empty<string>(); // corrupt per-client policy: fail closed
            }
        }

        return authOptions.Value.IntrospectionPermissions.TryGetValue(caller.ClientId, out var allowed)
            ? allowed
            : Array.Empty<string>();
    }

    /// <summary>Splits a stored audience value (single value, space-delimited, or JSON array) into audiences.</summary>
    public static IReadOnlyCollection<string> ParseStoredAudience(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Array.Empty<string>();
        var trimmed = stored.Trim();
        if (trimmed.StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<string[]>(trimmed) ?? Array.Empty<string>(); }
            catch (JsonException) { return Array.Empty<string>(); }
        }
        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
