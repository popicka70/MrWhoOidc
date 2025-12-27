using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using System;
using System.Linq;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of IOpaqueTokenPolicy that uses AuthOptions to make decisions.
/// </summary>
public sealed class OpaqueTokenPolicy(IOptions<AuthOptions> authOptions) : IOpaqueTokenPolicy
{
    public bool ShouldUseOpaqueAccessToken(string? audience)
    {
        var options = authOptions.Value.OpaqueAccessTokens;
        if (options == null || !options.Enabled)
        {
            return false;
        }

        // If no audiences specified, it's enabled for all
        if (options.Audiences == null || options.Audiences.Length == 0)
        {
            return true;
        }

        // If audience is null but we have a list, we can't match
        if (string.IsNullOrEmpty(audience))
        {
            return false;
        }

        return options.Audiences.Contains(audience, StringComparer.Ordinal);
    }
}
