namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Configuration options for platform admin authorization.
/// </summary>
public sealed class PlatformAdminAuthOptions
{
    /// <summary>
    /// The realm name where platform admin roles are defined.
    /// Default is "platform" (a special realm for platform-level administrators).
    /// </summary>
    public string RealmName { get; set; } = "platform";

    /// <summary>
    /// The role name that grants platform admin privileges.
    /// Default is "platform-admin".
    /// </summary>
    public string PlatformAdminRoleName { get; set; } = "platform-admin";
}
