using System.Collections.Generic;
using System.Security.Claims;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Consolidates logic for building role claims in tokens.
/// </summary>
public interface IRoleClaimBuilder
{
    /// <summary>
    /// Builds role claims for the provided role names.
    /// </summary>
    IEnumerable<Claim> BuildRoleClaims(string[]? roleNames);
}
