namespace MrWhoOidc.Auth.Services;

/// <summary>
/// The realm names the tenant seeder creates and the system depends on.
/// <para>
/// The admin realm holds the administrator roles and the seeded admin user, the default realm is the
/// namespace tenant clients and roles live in, and the platform realm defines the platform-admin role
/// (see <c>PlatformRealmWriteGuard</c>). Deleting any of them breaks the tenant, so the admin UI and the
/// admin API must refuse to delete a realm with one of these names. The seeder creates them with exactly
/// these names; both sides reference this class so the definitions cannot drift apart.
/// </para>
/// </summary>
public static class SystemRealms
{
    /// <summary>The realm that holds the administrator roles and the seeded admin user.</summary>
    public const string Admin = "admin";

    /// <summary>The default realm tenant clients, roles and users are provisioned into.</summary>
    public const string Default = "default";

    /// <summary>The realm that defines the platform-admin role for cross-tenant administration.</summary>
    public const string Platform = "platform";

    /// <summary>True when a realm with this name is a seeded system realm that must not be deleted.</summary>
    public static bool IsProtected(string realmName) =>
        string.Equals(realmName, Admin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(realmName, Default, StringComparison.OrdinalIgnoreCase)
        || string.Equals(realmName, Platform, StringComparison.OrdinalIgnoreCase);
}
