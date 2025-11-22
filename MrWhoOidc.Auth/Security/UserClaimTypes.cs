namespace MrWhoOidc.Auth.Security;

/// <summary>
/// Well-known custom claim types used by the MrWho OIDC platform.
/// </summary>
public static class UserClaimTypes
{
    /// <summary>
    /// Represents the decoupled user account identifier. Mirrors the legacy user identifier for now.
    /// </summary>
    public const string UserAccountId = "urn:mrwho:user-account-id";
}
