using Microsoft.AspNetCore.Authorization;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Authorization requirement for tenant admin access.
/// Requires that the user has the tenant-admin role in the current tenant's default realm.
/// Platform admins automatically satisfy this requirement.
/// </summary>
public sealed class TenantAdminRequirement : IAuthorizationRequirement { }
