using System;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Resolves token lifetimes based on tenant settings and client-specific overrides.
/// </summary>
public interface ITokenLifetimeResolver
{
    /// <summary>
    /// Resolves the access token lifetime for a specific client and tenant settings.
    /// </summary>
    TimeSpan ResolveAccessTokenLifetime(Client client, TenantSettings settings);

    /// <summary>
    /// Resolves the identity token lifetime for a specific client and tenant settings.
    /// </summary>
    TimeSpan ResolveIdentityTokenLifetime(Client client, TenantSettings settings);

    /// <summary>
    /// Resolves the refresh token lifetime for a specific client and tenant settings.
    /// </summary>
    TimeSpan ResolveRefreshTokenLifetime(Client client, TenantSettings settings);
}
