using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// An id_token_hint must be an ID token. Access tokens and logout tokens are signed with the same key, so a valid
/// signature alone does not make a token acceptable as a hint (it would let a token leaked to an API drive logout
/// or CIBA for its subject).
/// </summary>
public static class IdTokenHintPolicy
{
    // Members that ID tokens never carry but access tokens (RFC 9068) and logout tokens do.
    private static readonly string[] NonIdTokenClaims = ["events", "scope", "client_id"];

    public static bool IsIdToken(string? typ, ClaimsPrincipal principal)
    {
        if (IsTyp(typ, SecurityConstants.JwtTokenTypes.AtJwt) || IsTyp(typ, SecurityConstants.JwtTokenTypes.LogoutJwt))
        {
            return false;
        }

        return !principal.Claims.Any(c => NonIdTokenClaims.Contains(c.Type, StringComparer.Ordinal));
    }

    public static bool IsIdTokenHint(string idTokenHint, ClaimsPrincipal principal)
    {
        string? typ;
        try
        {
            typ = new JwtSecurityTokenHandler().ReadJwtToken(idTokenHint).Header.Typ;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return IsIdToken(typ, principal);
    }

    // typ is compared case-insensitively and may carry the "application/" media type prefix (RFC 7515 §4.1.9).
    private static bool IsTyp(string? typ, string expected)
    {
        if (string.IsNullOrEmpty(typ)) return false;
        const string Prefix = "application/";
        var value = typ.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? typ[Prefix.Length..] : typ;
        return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
