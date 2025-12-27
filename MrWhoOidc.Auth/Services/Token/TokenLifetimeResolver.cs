using System;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Settings;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of ITokenLifetimeResolver that consolidates lifetime calculation logic.
/// </summary>
public sealed class TokenLifetimeResolver : ITokenLifetimeResolver
{
    private const int DefaultAccessTokenLifetimeSeconds = 3600;
    private const int DefaultIdentityTokenLifetimeSeconds = 300;
    private const int DefaultRefreshTokenLifetimeSeconds = 1209600; // 14 days

    public TimeSpan ResolveAccessTokenLifetime(Client client, TenantSettings settings)
    {
        // M2M clients might have specific overrides
        if (client.M2MAccessTokenLifetimeSeconds.HasValue && client.M2MAccessTokenLifetimeSeconds.Value > 0)
        {
            return TimeSpan.FromSeconds(client.M2MAccessTokenLifetimeSeconds.Value);
        }

        // Standard access token lifetime from settings or default
        var seconds = settings.Tokens?.AccessTokenLifetimeSeconds ?? DefaultAccessTokenLifetimeSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    public TimeSpan ResolveIdentityTokenLifetime(Client client, TenantSettings settings)
    {
        // Future: could add client-specific ID token lifetime if needed
        var seconds = settings.Tokens?.IdTokenLifetimeSeconds ?? DefaultIdentityTokenLifetimeSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    public TimeSpan ResolveRefreshTokenLifetime(Client client, TenantSettings settings)
    {
        // Future: could add client-specific refresh token lifetime if needed
        var seconds = settings.Tokens?.RefreshTokenLifetimeSeconds ?? DefaultRefreshTokenLifetimeSeconds;
        return TimeSpan.FromSeconds(seconds);
    }
}
