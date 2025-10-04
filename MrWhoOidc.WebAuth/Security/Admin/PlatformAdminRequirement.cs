using Microsoft.AspNetCore.Authorization;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Authorization requirement for platform administrators who can manage tenants.
/// </summary>
public sealed class PlatformAdminRequirement : IAuthorizationRequirement { }
