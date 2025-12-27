using MrWhoOidc.Auth.Protocols;
using System.Collections.Generic;
using System.Security.Claims;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of IRoleClaimBuilder that uses standard OIDC role claim types.
/// </summary>
public sealed class RoleClaimBuilder : IRoleClaimBuilder
{
    public IEnumerable<Claim> BuildRoleClaims(string[]? roleNames)
    {
        if (roleNames == null || roleNames.Length == 0)
        {
            yield break;
        }

        foreach (var role in roleNames)
        {
            yield return new Claim(OidcConstants.Claims.Roles, role);
        }
    }
}
