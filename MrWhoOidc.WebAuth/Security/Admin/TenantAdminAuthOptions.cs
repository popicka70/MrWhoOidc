namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Configuration options for tenant admin authorization.
/// </summary>
public sealed class TenantAdminAuthOptions
{
    /// <summary>
    /// The name of the realm where tenant admin roles are checked (default: "default").
    /// This is the realm name within each tenant where the tenant-admin role must exist.
    /// </summary>
    public string RealmName { get; set; } = "default";
    
    /// <summary>
    /// The name of the role that grants tenant admin access (default: "tenant-admin").
    /// </summary>
    public string TenantAdminRoleName { get; set; } = "tenant-admin";
}
