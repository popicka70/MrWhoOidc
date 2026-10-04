using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// ADR-0010: admin API bearer tokens are a separate kind of token. They carry <see cref="Resource"/> as audience and
/// <see cref="Scope"/>, are requested explicitly, and only designated admin clients (the CLI) can obtain them.
/// Ordinary RP tokens (aud=api) are not accepted by the admin APIs, and admin tokens are useless at RPs.
/// </summary>
public static class AdminApiAccess
{
    public const string Resource = "urn:mrwho:admin-api";
    public const string Scope = "mrwho:admin";

    /// <summary>
    /// Only a system client with <see cref="Client.AllowAdminApi"/> may obtain admin API tokens. Neither flag can be
    /// set by a tenant admin: both are written only by <c>CliClientService</c>.
    /// </summary>
    public static bool ClientMayObtain(Client? client) => client is { IsSystemClient: true, AllowAdminApi: true };
}
