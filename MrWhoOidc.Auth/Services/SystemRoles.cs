namespace MrWhoOidc.Auth.Services;

/// <summary>
/// The role names the tenant seeder creates and the authorization policies depend on.
/// <para>
/// Policies such as <c>tenant-admin</c> and <c>platform-admin</c> match on role <i>names</i> (the claims they
/// mint), regardless of which realm the role lives in. Deleting or renaming a role with one of these names
/// therefore breaks tenant administration — the role-based twin of deleting a system realm. The admin UI and
/// the admin API must refuse to delete or rename such roles. The seeder creates them with exactly these
/// names; both sides reference this class so the definitions cannot drift apart.
/// </para>
/// </summary>
public static class SystemRoles
{
    /// <summary>The tenant administrator role in the admin realm; grants the tenant-admin UI.</summary>
    public const string Admin = "admin";

    /// <summary>The licensing administrator role in the admin realm.</summary>
    public const string LicensingAdmin = "licensing-admin";

    /// <summary>The tenant-admin policy role in the default realm.</summary>
    public const string TenantAdmin = "tenant-admin";

    /// <summary>The platform-admin policy role in the platform realm; grants cross-tenant administration.</summary>
    public const string PlatformAdmin = "platform-admin";

    /// <summary>True when a role with this name is a seeded system role that must not be deleted or renamed.</summary>
    public static bool IsProtected(string roleName) =>
        string.Equals(roleName, Admin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(roleName, LicensingAdmin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(roleName, TenantAdmin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(roleName, PlatformAdmin, StringComparison.OrdinalIgnoreCase);
}
