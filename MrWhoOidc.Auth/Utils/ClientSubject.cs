using System.Security.Claims;

namespace MrWhoOidc.Auth.Utils;

/// <summary>
/// Keeps client identities and user identities apart.
/// A client_credentials access token carries <c>sub = client_id</c> (RFC 9068 §2.2). A tenant admin chooses
/// client ids, so without these rules a client named after a user's GUID was read as that user (V1).
/// </summary>
public static class ClientSubject
{
    /// <summary>
    /// True when the token was issued to a client acting for itself, so it names no user.
    /// </summary>
    public static bool IsClientToken(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst("sub")?.Value;
        var clientId = principal.FindFirst("client_id")?.Value;
        return !string.IsNullOrEmpty(sub) && string.Equals(sub, clientId, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client id may not look like a user id (any format <see cref="Guid.TryParse(string?, out Guid)"/> accepts).
    /// </summary>
    public static bool IsReservedClientId(string? clientId)
        => !string.IsNullOrWhiteSpace(clientId) && Guid.TryParse(clientId.Trim(), out _);

    public const string ReservedClientIdMessage = "Client ID must not be a GUID; GUIDs are reserved for user identifiers.";
}
